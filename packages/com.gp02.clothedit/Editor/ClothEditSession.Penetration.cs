using System;
using System.Collections.Generic;
using System.Diagnostics;
using GP02.Core;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace GP02.EditorTools
{
    // 貫通（体が布を突き抜けている所）の検出と、布で押し出して直すこと。
    // 体は、隙間の測定と同じ相手（同じアバターの、まとめて編集する布以外のレンダラー）から、布の内側にある部品だけを使う。
    public sealed partial class ClothEditSession
    {
        PenetrationDetector _penetrationDetector;
        PenetrationReport _penetration;
        int _penetrationVersion = -1;

        /// <summary>
        /// 体の表面（同じアバターのほかのレンダラーのうち、布の内側にある部品）。見つからなければ null。
        /// ソルバーにも渡してあり、布は編集中ずっと体の中へ入らない（すでに入っている所は、直すまでそのまま）。
        /// </summary>
        public BodySurface Body { get; private set; }

        /// <summary>体を集めて作るのにかかった時間（ミリ秒）。</summary>
        public double BodyMilliseconds { get; private set; }

        /// <summary>体の説明（ログ・画面用）。使った部品の数など。</summary>
        public string BodyDescription { get; private set; } = "";

        /// <summary>最後に調べた貫通の結果。まだ調べていなければ null。</summary>
        public PenetrationReport LastPenetration => _penetration;

        /// <summary>最後に直した結果。まだ直していなければ null。</summary>
        public PenetrationFixResult LastPenetrationFix { get; private set; }

        /// <summary>調べたあとに形が変わったか（結果が古いかもしれない）。</summary>
        public bool IsPenetrationStale => _penetration != null && _penetrationVersion != _shapeVersion;

        int _penetrationPins = -1;

        /// <summary>調べたあとに、ピン留めの数が変わったか（結果の「うちピン留め N 点」が古いかもしれない）。</summary>
        public bool IsPenetrationPinStale => _penetration != null && _penetrationPins != _pinnedCount;

        // コンストラクターから呼ぶ。ほかのレンダラーを集め、体の表面を作ってソルバーに渡す。集めたものは隙間の測定にも使う
        OtherRendererMeshes PrepareBody()
        {
            var timer = Stopwatch.StartNew();
            OtherRendererMeshes others = OtherRendererMeshes.Collect(_renderers);
            Body = BuildBody(others, _solver.Positions, out string description);
            BodyDescription = description;
            _solver.SetBody(Body);
            BodyMilliseconds = timer.Elapsed.TotalMilliseconds;
            ClothEditLog.Info($"[GP02] {DescribeTargets()}: {description}（{BodyMilliseconds:F0} ms）");
            return others;
        }

        // 布の点（今の粒子の位置）から見て、布の内側にある部品だけを使う体の表面。使う部品が無ければ null
        static BodySurface BuildBody(OtherRendererMeshes others, ReadOnlySpan<NVector3> clothPoints, out string description)
        {
            if (others == null || others.TriangleCount == 0)
            {
                description = "体: 同じアバターのほかのレンダラーが無いので、貫通は調べません";
                return null;
            }
            var body = new BodySurface(others.Positions, others.Triangles);
            int enabled = body.ClassifyIslands(clothPoints, BodySurface.DefaultMaxDistance);
            int solid = 0, enabledTriangles = 0;
            for (int island = 0; island < body.IslandCount; island++)
            {
                if (body.IsIslandSolid(island)) solid++;
                if (body.IsIslandEnabled(island)) enabledTriangles += body.GetIslandTriangleCount(island);
            }
            description = $"体: ほかのレンダラー {others.RendererCount} 個・部品 {body.IslandCount} 個（中のある部品 {solid}）のうち、布の内側にある部品 {enabled} 個・三角形 {enabledTriangles} を使う";
            return enabled > 0 ? body : null;
        }

        /// <summary>
        /// 体を集め直す（体のシェイプキーの重みやポーズを変えたあと）。ソルバーの体も入れ替え、今の形で、すでに入っている所はそのままにする。
        /// 元の形で体の中に入った粒子は、体や内側の布までの隙間を 0 にする。体が見つかれば true。
        /// </summary>
        public bool RebuildBody()
        {
            var timer = Stopwatch.StartNew();
            OtherRendererMeshes others = OtherRendererMeshes.Collect(_renderers);
            Body = BuildBody(others, _solver.Positions, out string description);
            BodyDescription = description;
            _solver.SetBody(Body);
            if (Body != null)
            {
                NVector3[] rest = _cloth.Model.RestPositions;
                for (int i = 0; i < rest.Length; i++)
                {
                    if (!Body.TryQuery(rest[i], BodySurface.DefaultMaxDistance, out BodyHit hit) || hit.SignedDistance >= 0f) continue;
                    if (RestAllowance != null) RestAllowance[i] = 0f;
                    if (RestAllowanceWithoutLayers != null) RestAllowanceWithoutLayers[i] = 0f;
                }
            }
            BodyMilliseconds = timer.Elapsed.TotalMilliseconds;
            ClothEditLog.Info($"[GP02] {DescribeTargets()}: 体を集め直しました。{description}（{BodyMilliseconds:F0} ms）");
            return Body != null;
        }

        /// <summary>今の形で、体が布を突き抜けている所を調べる。結果は <see cref="LastPenetration"/> にも残る。</summary>
        public PenetrationReport DetectPenetration(PenetrationOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (_penetrationDetector == null) _penetrationDetector = new PenetrationDetector(_cloth);
            if (_penetration == null) _penetration = new PenetrationReport();
            _penetrationDetector.Detect(_solver, Body, options, _penetration);
            _penetrationVersion = _shapeVersion;
            _penetrationPins = _pinnedCount;
            return _penetration;
        }

        /// <summary>
        /// 体が布を突き抜けている所を、布の長さを保ったまま体の外（余白）へ押し出す（<see cref="PenetrationFixer"/>）。仕上げまで行う。
        /// つまみは外れる。Undo の記録は呼び出し側（<see cref="ClothEditController.FixPenetration"/>）が取る。最後に調べた結果は <see cref="LastPenetration"/>。
        /// </summary>
        public PenetrationFixResult FixPenetration(PenetrationOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _grabParticle = -1;
            _grabSet = null;
            _solver.Release();
            if (_penetrationDetector == null) _penetrationDetector = new PenetrationDetector(_cloth);
            if (_penetration == null) _penetration = new PenetrationReport();
            PenetrationFixResult result = PenetrationFixer.Fix(_penetrationDetector, _solver, Body, options, _penetration);
            UpdateCurrentShape();
            _penetrationVersion = _shapeVersion;
            _penetrationPins = _pinnedCount;
            LastPenetrationFix = result;
            return result;
        }

        /// <summary>
        /// 表示用に、最後に調べた結果の、突き抜けている所の点（布の粒子は今の位置、布の外へ出た体の頂点）を result に入れる（先に空にする）。
        /// maxCount を超えるときは間引く。
        /// </summary>
        public void GetPenetrationPoints(List<Vector3> result, int maxCount)
        {
            result.Clear();
            if (_penetration == null || maxCount <= 0) return;
            List<int> particles = _penetration.PointParticles;
            List<NVector3> vertices = _penetration.PointBodyVertices;
            int total = particles.Count + vertices.Count;
            int stride = total <= maxCount ? 1 : (total + maxCount - 1) / maxCount;
            ReadOnlySpan<NVector3> positions = _solver.Positions;
            for (int k = 0; k < particles.Count && result.Count < maxCount; k += stride)
            {
                int i = particles[k];
                if ((uint)i < (uint)positions.Length) result.Add(new Vector3(positions[i].X, positions[i].Y, positions[i].Z));
            }
            for (int k = 0; k < vertices.Count && result.Count < maxCount; k += stride) result.Add(new Vector3(vertices[k].X, vertices[k].Y, vertices[k].Z));
        }
    }
}
