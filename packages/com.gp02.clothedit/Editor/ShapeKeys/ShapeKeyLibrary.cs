using System;
using System.Collections.Generic;
using System.IO;
using GP02.Core;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace GP02.EditorTools
{
    /// <summary>
    /// シェイプキーの編集データ（<see cref="GP02ShapeKeyData"/>）と Unity のメッシュの間の変換と、データの作成・保存。
    /// 差分の計算そのものは計算の中核（<see cref="ShapeKeyMath"/>）で行い、ここは Unity の型との受け渡しをする。
    /// メッシュを返す関数は、元のメッシュを変更せず複製を返す（アセットにはしない。保存は呼び出し側）。
    /// </summary>
    public static class ShapeKeyLibrary
    {
        public const string DataFolder = "Assets/GP02/Data";
        const string MeshSuffix = "_GP02";
        const string BlendShapeCurvePrefix = "blendShape.";
        const float MaxGridSearchRadius = 0.32f;

        // ------------------------------------------------------------------
        // メッシュへの書き出し
        // ------------------------------------------------------------------

        /// <summary>
        /// 元のメッシュの複製に、有効なエントリを BlendShape として加えたメッシュを返す。元の BlendShape はそのまま残る。
        /// in-between のフレームも入れる。
        /// </summary>
        /// <param name="overwriteSameName">
        /// true なら、同じ名前の BlendShape を置き換える（順番は保つ）。
        /// false なら、「名前 (1)」のように別の名前で加える
        /// </param>
        /// <exception cref="ArgumentException">データの頂点数がメッシュと違う、エントリの名前が空、差分の長さが違うとき</exception>
        public static Mesh BuildMesh(Mesh source, GP02ShapeKeyData data, bool overwriteSameName)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (data == null) throw new ArgumentNullException(nameof(data));
            int vertexCount = source.vertexCount;
            if (data.VertexCount != vertexCount)
            {
                throw new ArgumentException($"シェイプキーのデータ（頂点 {data.VertexCount} 個）と、メッシュ「{source.name}」（頂点 {vertexCount} 個）の頂点数が違います。", nameof(data));
            }

            // 追加する分と、置き換える分を先に決める
            var usedNames = new HashSet<string>();
            for (int i = 0; i < source.blendShapeCount; i++) usedNames.Add(source.GetBlendShapeName(i));
            var replaceAt = new Dictionary<int, ShapeKeyEntry>();
            var appended = new List<KeyValuePair<string, ShapeKeyEntry>>();
            for (int e = 0; e < data.Entries.Count; e++)
            {
                ShapeKeyEntry entry = data.Entries[e];
                if (entry == null || !entry.Enabled || entry.Frames.Count == 0) continue;
                if (string.IsNullOrEmpty(entry.Name)) throw new ArgumentException($"{e} 番目のシェイプキーの名前が空です。", nameof(data));
                ValidateEntry(entry, vertexCount);

                if (overwriteSameName)
                {
                    int sourceIndex = source.GetBlendShapeIndex(entry.Name);
                    if (sourceIndex >= 0)
                    {
                        replaceAt[sourceIndex] = entry;
                        continue;
                    }
                    int appendedIndex = appended.FindIndex(pair => pair.Key == entry.Name);
                    if (appendedIndex >= 0)
                    {
                        appended[appendedIndex] = new KeyValuePair<string, ShapeKeyEntry>(entry.Name, entry);
                        continue;
                    }
                    appended.Add(new KeyValuePair<string, ShapeKeyEntry>(entry.Name, entry));
                }
                else
                {
                    string name = entry.Name;
                    for (int n = 1; usedNames.Contains(name); n++) name = $"{entry.Name} ({n})";
                    usedNames.Add(name);
                    appended.Add(new KeyValuePair<string, ShapeKeyEntry>(name, entry));
                }
            }

            Mesh mesh = Object.Instantiate(source);
            mesh.name = NameWithSuffix(source.name);
            if (replaceAt.Count > 0)
            {
                // BlendShape を1つだけ差し替える API は無いので、全部消して順に入れ直す。
                // 全部を一度に読むと大きいので、元のメッシュから1つずつ読む
                mesh.ClearBlendShapes();
                for (int i = 0; i < source.blendShapeCount; i++)
                {
                    if (replaceAt.TryGetValue(i, out ShapeKeyEntry replacement)) AddEntryAsBlendShape(mesh, source.GetBlendShapeName(i), replacement);
                    else CopyBlendShape(source, i, mesh);
                }
            }
            foreach (var pair in appended) AddEntryAsBlendShape(mesh, pair.Key, pair.Value);
            return mesh;
        }

        static string NameWithSuffix(string name) => name.EndsWith(MeshSuffix) ? name : name + MeshSuffix;

        static void ValidateEntry(ShapeKeyEntry entry, int vertexCount)
        {
            foreach (ShapeKeyFrame frame in entry.Frames)
            {
                if (frame.DeltaVertices == null || frame.DeltaVertices.Length != vertexCount)
                {
                    throw new ArgumentException($"シェイプキー「{entry.Name}」の差分の数（{frame.DeltaVertices?.Length ?? 0}）が、頂点数（{vertexCount}）と違います。");
                }
                if (frame.HasNormals && frame.DeltaNormals.Length != vertexCount) throw new ArgumentException($"シェイプキー「{entry.Name}」の法線の差分の数が、頂点数と違います。");
                if (frame.HasTangents && frame.DeltaTangents.Length != vertexCount) throw new ArgumentException($"シェイプキー「{entry.Name}」の接線の差分の数が、頂点数と違います。");
            }
        }

        static void AddEntryAsBlendShape(Mesh mesh, string name, ShapeKeyEntry entry)
        {
            // Unity は、フレームを重みの小さい順に加えることを求める
            var frames = new List<ShapeKeyFrame>(entry.Frames);
            frames.Sort((a, b) => a.Weight.CompareTo(b.Weight));
            foreach (ShapeKeyFrame frame in frames)
            {
                mesh.AddBlendShapeFrame(name, frame.Weight, frame.DeltaVertices,
                    frame.HasNormals ? frame.DeltaNormals : null, frame.HasTangents ? frame.DeltaTangents : null);
            }
        }

        static void CopyBlendShape(Mesh from, int index, Mesh to)
        {
            int count = from.vertexCount;
            var vertices = new Vector3[count];
            var normals = new Vector3[count];
            var tangents = new Vector3[count];
            string name = from.GetBlendShapeName(index);
            for (int f = 0; f < from.GetBlendShapeFrameCount(index); f++)
            {
                from.GetBlendShapeFrameVertices(index, f, vertices, normals, tangents);
                to.AddBlendShapeFrame(name, from.GetBlendShapeFrameWeight(index, f), vertices, normals, tangents);
            }
        }

        // ------------------------------------------------------------------
        // 素体への焼き込み
        // ------------------------------------------------------------------

        /// <summary>
        /// 頂点を、各エントリの差分 × 重みだけ動かした新しいメッシュを返す（法線・接線も差分で動かす）。既存の BlendShape は残る。
        /// 重みの扱いは Unity の BlendShape と同じ（フレームの間は線形に補間する）。
        /// </summary>
        /// <param name="weights">エントリごとの重み（0〜100）。null ならエントリの <see cref="ShapeKeyEntry.PreviewWeight"/></param>
        public static Mesh BakeIntoBase(Mesh source, IReadOnlyList<ShapeKeyEntry> entries, IReadOnlyList<float> weights)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            if (weights != null && weights.Count != entries.Count) throw new ArgumentException("エントリと重みの数が一致しません。", nameof(weights));

            int count = source.vertexCount;
            Vector3[] vertices = source.vertices;
            Vector3[] normals = source.normals;
            Vector4[] tangents = source.tangents;
            bool hasNormals = normals.Length == count;
            bool hasTangents = tangents.Length == count;

            for (int e = 0; e < entries.Count; e++)
            {
                ShapeKeyEntry entry = entries[e];
                if (entry == null || entry.Frames.Count == 0) continue;
                ValidateEntry(entry, count);
                float weight = weights != null ? weights[e] : entry.PreviewWeight;
                if (weight == 0f) continue;

                Vector3[] dv = EvaluateChannel(entry, weight, count, frame => frame.DeltaVertices);
                for (int v = 0; v < count; v++) vertices[v] += dv[v];
                if (hasNormals && AnyFrame(entry, frame => frame.HasNormals))
                {
                    Vector3[] dn = EvaluateChannel(entry, weight, count, frame => frame.DeltaNormals);
                    for (int v = 0; v < count; v++) normals[v] += dn[v];
                }
                if (hasTangents && AnyFrame(entry, frame => frame.HasTangents))
                {
                    Vector3[] dt = EvaluateChannel(entry, weight, count, frame => frame.DeltaTangents);
                    for (int v = 0; v < count; v++) tangents[v] += new Vector4(dt[v].x, dt[v].y, dt[v].z, 0f);
                }
            }

            Mesh mesh = Object.Instantiate(source);
            mesh.name = NameWithSuffix(source.name);
            mesh.vertices = vertices;
            if (hasNormals)
            {
                for (int v = 0; v < count; v++) normals[v] = normals[v].sqrMagnitude > 1e-12f ? normals[v].normalized : normals[v];
                mesh.normals = normals;
            }
            if (hasTangents)
            {
                for (int v = 0; v < count; v++)
                {
                    var xyz = new Vector3(tangents[v].x, tangents[v].y, tangents[v].z);
                    if (xyz.sqrMagnitude > 1e-12f) xyz.Normalize();
                    tangents[v] = new Vector4(xyz.x, xyz.y, xyz.z, tangents[v].w);
                }
                mesh.tangents = tangents;
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        static bool AnyFrame(ShapeKeyEntry entry, Func<ShapeKeyFrame, bool> predicate)
        {
            foreach (ShapeKeyFrame frame in entry.Frames)
            {
                if (predicate(frame)) return true;
            }
            return false;
        }

        // 1つのチャンネル（位置・法線・接線）の差分を、重みで評価する。空のフレームは 0 として扱う
        static Vector3[] EvaluateChannel(ShapeKeyEntry entry, float weight, int count, Func<ShapeKeyFrame, Vector3[]> channel)
        {
            var frames = new List<ShapeFrame>(entry.Frames.Count);
            foreach (ShapeKeyFrame frame in entry.Frames)
            {
                Vector3[] array = channel(frame);
                frames.Add(new ShapeFrame(frame.Weight, array != null && array.Length == count ? MeshBridge.ToNumerics(array, Vector3.one) : new NVector3[count]));
            }
            frames.Sort((a, b) => a.Weight.CompareTo(b.Weight));
            return MeshBridge.ToUnity(ShapeKeyMath.EvaluateFrames(frames, weight, count), Vector3.one);
        }

        // ------------------------------------------------------------------
        // 取り込み
        // ------------------------------------------------------------------

        /// <summary>メッシュの BlendShape を、全フレームごとエントリに取り込む。名前は BlendShape の名前になる。</summary>
        public static ShapeKeyEntry ImportFromBlendShape(Mesh mesh, int shapeIndex)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (shapeIndex < 0 || shapeIndex >= mesh.blendShapeCount)
            {
                throw new ArgumentOutOfRangeException(nameof(shapeIndex), $"メッシュ「{mesh.name}」に {shapeIndex} 番目の BlendShape がありません（全部で {mesh.blendShapeCount} 個）。");
            }

            int count = mesh.vertexCount;
            var entry = new ShapeKeyEntry
            {
                Name = mesh.GetBlendShapeName(shapeIndex),
                // Unity のフレームは、その重みでの形そのもの（絶対の差分）
                InBetweenMode = ShapeKeyInBetweenMode.Crossfade,
            };
            for (int f = 0; f < mesh.GetBlendShapeFrameCount(shapeIndex); f++)
            {
                var vertices = new Vector3[count];
                var normals = new Vector3[count];
                var tangents = new Vector3[count];
                mesh.GetBlendShapeFrameVertices(shapeIndex, f, vertices, normals, tangents);
                entry.Frames.Add(new ShapeKeyFrame
                {
                    Weight = mesh.GetBlendShapeFrameWeight(shapeIndex, f),
                    DeltaVertices = vertices,
                    DeltaNormals = EmptyIfZero(normals),
                    DeltaTangents = EmptyIfZero(tangents),
                });
            }
            return entry;
        }

        /// <summary>
        /// 編集済みのメッシュ（別に書き出したメッシュなど）と元のメッシュの頂点の差を、重み 100 の1フレームのエントリにする。
        /// 法線・接線は、両方のメッシュにあるときだけ差分を取る。名前は編集済みのメッシュの名前。
        /// </summary>
        /// <exception cref="ArgumentException">頂点数が違うとき（頂点の並びが同じメッシュでなければ差を取れない）</exception>
        public static ShapeKeyEntry ImportFromMesh(Mesh source, Mesh edited)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (edited == null) throw new ArgumentNullException(nameof(edited));
            if (source.vertexCount != edited.vertexCount)
            {
                throw new ArgumentException(
                    $"頂点の数が違うので取り込めません。元のメッシュ「{source.name}」は {source.vertexCount} 個、取り込むメッシュ「{edited.name}」は {edited.vertexCount} 個です。" +
                    "元のメッシュを編集して書き出した、頂点の並びが同じメッシュを指定してください。", nameof(edited));
            }

            int count = source.vertexCount;
            Vector3[] a = source.vertices, b = edited.vertices;
            var deltas = new Vector3[count];
            for (int v = 0; v < count; v++) deltas[v] = b[v] - a[v];

            var frame = new ShapeKeyFrame { Weight = ShapeKeyMath.FullWeight, DeltaVertices = deltas };
            Vector3[] sn = source.normals, en = edited.normals;
            if (sn.Length == count && en.Length == count)
            {
                var d = new Vector3[count];
                for (int v = 0; v < count; v++) d[v] = en[v] - sn[v];
                frame.DeltaNormals = EmptyIfZero(d);
            }
            Vector4[] st = source.tangents, et = edited.tangents;
            if (st.Length == count && et.Length == count)
            {
                var d = new Vector3[count];
                for (int v = 0; v < count; v++) d[v] = new Vector3(et[v].x - st[v].x, et[v].y - st[v].y, et[v].z - st[v].z);
                frame.DeltaTangents = EmptyIfZero(d);
            }

            var entry = new ShapeKeyEntry { Name = edited.name };
            entry.Frames.Add(frame);
            return entry;
        }

        /// <summary>
        /// アニメーションクリップの、指定した時刻の BlendShape の値を使って、メッシュの既存の BlendShape を合成したエントリを作る。
        /// 表情のクリップから、その表情の形をシェイプキー（重み 100 の1フレーム）として取り出すのに使う。
        /// 名前はクリップの名前。
        /// </summary>
        /// <param name="rendererPath">クリップの中での SkinnedMeshRenderer のパス（ルートのレンダラーなら空）</param>
        /// <param name="time">秒</param>
        /// <exception cref="InvalidOperationException">クリップにこのメッシュの BlendShape のカーブが無い（または値が全部 0 の）とき</exception>
        public static ShapeKeyEntry ImportFromAnimationClip(Mesh mesh, AnimationClip clip, string rendererPath, float time)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            rendererPath = rendererPath ?? "";

            int count = mesh.vertexCount;
            var vertices = new Vector3[count];
            var normals = new Vector3[count];
            var tangents = new Vector3[count];
            bool anyNormals = false, anyTangents = false;
            int used = 0;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != typeof(SkinnedMeshRenderer) || binding.path != rendererPath) continue;
                if (!binding.propertyName.StartsWith(BlendShapeCurvePrefix)) continue;
                int shape = mesh.GetBlendShapeIndex(binding.propertyName.Substring(BlendShapeCurvePrefix.Length));
                if (shape < 0) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null) continue;
                float weight = curve.Evaluate(time);
                if (weight == 0f) continue;

                ShapeKeyEntry shapeEntry = ImportFromBlendShape(mesh, shape);
                Accumulate(vertices, EvaluateChannel(shapeEntry, weight, count, frame => frame.DeltaVertices));
                if (AnyFrame(shapeEntry, frame => frame.HasNormals))
                {
                    anyNormals = true;
                    Accumulate(normals, EvaluateChannel(shapeEntry, weight, count, frame => frame.DeltaNormals));
                }
                if (AnyFrame(shapeEntry, frame => frame.HasTangents))
                {
                    anyTangents = true;
                    Accumulate(tangents, EvaluateChannel(shapeEntry, weight, count, frame => frame.DeltaTangents));
                }
                used++;
            }
            if (used == 0)
            {
                throw new InvalidOperationException(
                    $"アニメーションクリップ「{clip.name}」の {time} 秒に、パス「{rendererPath}」の SkinnedMeshRenderer が持つ、メッシュ「{mesh.name}」の BlendShape の値（0 以外）がありません。");
            }

            var entry = new ShapeKeyEntry { Name = clip.name };
            entry.Frames.Add(new ShapeKeyFrame
            {
                Weight = ShapeKeyMath.FullWeight,
                DeltaVertices = vertices,
                DeltaNormals = anyNormals ? normals : new Vector3[0],
                DeltaTangents = anyTangents ? tangents : new Vector3[0],
            });
            return entry;
        }

        static void Accumulate(Vector3[] total, Vector3[] add)
        {
            for (int i = 0; i < total.Length; i++) total[i] += add[i];
        }

        static Vector3[] EmptyIfZero(Vector3[] deltas)
        {
            foreach (Vector3 d in deltas)
            {
                if (d != Vector3.zero) return deltas;
            }
            return new Vector3[0];
        }

        // ------------------------------------------------------------------
        // エントリの作成と加工
        // ------------------------------------------------------------------

        /// <summary>
        /// 頂点ごとの差分から、重み 100 の1フレームのエントリを作る。
        /// computeNormals が true で、メッシュに法線があれば、動かした後の形の法線の差分も入れる。
        /// </summary>
        public static ShapeKeyEntry CreateEntryFromDeltas(Mesh source, string name, Vector3[] deltas, bool computeNormals = true)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (deltas == null) throw new ArgumentNullException(nameof(deltas));
            if (deltas.Length != source.vertexCount) throw new ArgumentException($"差分の数（{deltas.Length}）がメッシュの頂点数（{source.vertexCount}）と違います。", nameof(deltas));

            var frame = new ShapeKeyFrame { Weight = ShapeKeyMath.FullWeight, DeltaVertices = (Vector3[])deltas.Clone() };
            Vector3[] restNormals = source.normals;
            if (computeNormals && restNormals.Length == source.vertexCount)
            {
                ShapeKeyMath.RecomputeNormalDeltas(MeshBridge.ToNumerics(source.vertices, Vector3.one), source.triangles,
                    MeshBridge.ToNumerics(deltas, Vector3.one), MeshBridge.ToNumerics(restNormals, Vector3.one), out NVector3[] normalDeltas);
                frame.DeltaNormals = EmptyIfZero(MeshBridge.ToUnity(normalDeltas, Vector3.one));
            }
            var entry = new ShapeKeyEntry { Name = name };
            entry.Frames.Add(frame);
            return entry;
        }

        /// <summary>
        /// エントリの左右を反転した、新しいエントリを返す（全フレームの位置・法線・接線）。
        /// 鏡像の頂点が見つからない頂点の差分は 0 になる。
        /// </summary>
        /// <param name="axis">鏡の軸（0=X, 1=Y, 2=Z）</param>
        /// <param name="center">鏡の位置（メッシュ空間の軸の値）</param>
        /// <param name="tolerance">鏡像の頂点を探す距離（メートル）</param>
        public static ShapeKeyEntry FlipEntry(ShapeKeyEntry entry, Mesh source, int axis, float center, float tolerance, string newName = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (source == null) throw new ArgumentNullException(nameof(source));
            int[] map = VertexMirrorMap.Build(MeshBridge.ToNumerics(source.vertices, Vector3.one), axis, center, tolerance);
            return MapEntry(entry, newName ?? entry.Name, deltas => ToUnityArray(ShapeKeyMath.Flip(ToNumericsArray(deltas), map, axis)));
        }

        /// <summary>
        /// エントリを左右に分けた、新しい2つのエントリを返す。left + right は元のエントリに戻る。
        /// 名前は元の名前に leftSuffix・rightSuffix を付けたもの。
        /// </summary>
        /// <param name="blendWidth">中央付近をなめらかに分ける幅（メートル）</param>
        /// <param name="leftIsPositive">true なら軸の値が大きい側を left にする</param>
        public static void SplitEntry(ShapeKeyEntry entry, Mesh source, int axis, float center, float blendWidth,
            out ShapeKeyEntry left, out ShapeKeyEntry right, string leftSuffix = "_L", string rightSuffix = "_R", bool leftIsPositive = true)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (source == null) throw new ArgumentNullException(nameof(source));
            NVector3[] rest = MeshBridge.ToNumerics(source.vertices, Vector3.one);
            left = MapEntry(entry, entry.Name + leftSuffix, deltas =>
            {
                ShapeKeyMath.SplitLeftRight(ToNumericsArray(deltas), rest, axis, center, blendWidth, out NVector3[] l, out _, leftIsPositive);
                return ToUnityArray(l);
            });
            right = MapEntry(entry, entry.Name + rightSuffix, deltas =>
            {
                ShapeKeyMath.SplitLeftRight(ToNumericsArray(deltas), rest, axis, center, blendWidth, out _, out NVector3[] r, leftIsPositive);
                return ToUnityArray(r);
            });
        }

        static NVector3[] ToNumericsArray(Vector3[] source) => MeshBridge.ToNumerics(source, Vector3.one);
        static Vector3[] ToUnityArray(NVector3[] source) => MeshBridge.ToUnity(source, Vector3.one);

        // 全フレーム・全チャンネルに同じ操作をかけた、新しいエントリを作る（空のチャンネルは空のまま）
        static ShapeKeyEntry MapEntry(ShapeKeyEntry entry, string name, Func<Vector3[], Vector3[]> operation)
        {
            var result = new ShapeKeyEntry
            {
                Name = name,
                Enabled = entry.Enabled,
                Group = entry.Group,
                PreviewWeight = entry.PreviewWeight,
                InBetweenMode = entry.InBetweenMode,
                PinnedVertices = (int[])entry.PinnedVertices.Clone(),
            };
            foreach (ShapeKeyFrame frame in entry.Frames)
            {
                result.Frames.Add(new ShapeKeyFrame
                {
                    Weight = frame.Weight,
                    DeltaVertices = operation(frame.DeltaVertices),
                    DeltaNormals = frame.HasNormals ? operation(frame.DeltaNormals) : new Vector3[0],
                    DeltaTangents = frame.HasTangents ? operation(frame.DeltaTangents) : new Vector3[0],
                });
            }
            return result;
        }

        /// <summary>
        /// エントリを左右対称にした、新しいエントリを返す（全フレームの位置・法線・接線）。
        /// 片側を正として、反対側を鏡像で置き換える。鏡像の頂点が見つからない頂点は、そのまま残る。
        /// </summary>
        /// <param name="keepPositiveSide">true なら軸の値が center より大きい側を正とする</param>
        public static ShapeKeyEntry SymmetrizeEntry(ShapeKeyEntry entry, Mesh source, int axis, float center, float tolerance, bool keepPositiveSide, string newName = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (source == null) throw new ArgumentNullException(nameof(source));
            NVector3[] rest = MeshBridge.ToNumerics(source.vertices, Vector3.one);
            int[] map = VertexMirrorMap.Build(rest, axis, center, tolerance);
            return MapEntry(entry, newName ?? entry.Name,
                deltas => ToUnityArray(ShapeKeyMath.Symmetrize(ToNumericsArray(deltas), rest, map, axis, center, keepPositiveSide)));
        }

        /// <summary>エントリの差分を factor 倍した、新しいエントリを返す（全フレームの位置・法線・接線）。</summary>
        public static ShapeKeyEntry ScaleEntry(ShapeKeyEntry entry, float factor, string newName = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            return MapEntry(entry, newName ?? entry.Name, deltas => ToUnityArray(ShapeKeyMath.Scale(ToNumericsArray(deltas), factor)));
        }

        /// <summary>
        /// エントリの全フレームの法線の差分を、位置の差分から計算し直した新しいエントリを返す（位置・接線はそのまま）。
        /// 既存の BlendShape から取り込んだエントリの法線や、法線が壊れて見える（継ぎ目だけ陰影が違う）エントリを直すのに使う。
        /// 動かしても法線が変わらなければ、そのフレームの法線の差分は空になる。
        /// </summary>
        /// <param name="source">元の形のメッシュ（法線を持っていること）</param>
        /// <param name="smoothAngleDegrees">同じ位置の頂点の面をなめらかにつなぐ角度（<see cref="ShapeKeyMath.RecomputeNormalDeltas"/>）</param>
        /// <param name="mergeCoincident">同じ位置の頂点を結合して計算するか</param>
        /// <exception cref="InvalidOperationException">メッシュに法線が無いとき</exception>
        public static ShapeKeyEntry RecomputeNormalsEntry(ShapeKeyEntry entry, Mesh source, float smoothAngleDegrees, bool mergeCoincident)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (source == null) throw new ArgumentNullException(nameof(source));
            Vector3[] restNormals = source.normals;
            if (restNormals.Length != source.vertexCount)
            {
                throw new InvalidOperationException($"元のメッシュ「{source.name}」に法線がありません。法線の差分は、メッシュの法線に足して使うため、法線があるメッシュでしか計算できません。");
            }
            NVector3[] rest = MeshBridge.ToNumerics(source.vertices, Vector3.one);
            int[] triangles = source.triangles;
            NVector3[] restNormalsNumerics = MeshBridge.ToNumerics(restNormals, Vector3.one);

            ShapeKeyEntry result = CloneEntry(entry);
            foreach (ShapeKeyFrame frame in result.Frames)
            {
                ShapeKeyMath.RecomputeNormalDeltas(rest, triangles, ToNumericsArray(frame.DeltaVertices), restNormalsNumerics,
                    out NVector3[] normalDeltas, smoothAngleDegrees, mergeCoincident);
                frame.DeltaNormals = EmptyIfZero(ToUnityArray(normalDeltas));
            }
            return result;
        }

        /// <summary>エントリの深いコピー（差分の配列も複製する）。</summary>
        public static ShapeKeyEntry CloneEntry(ShapeKeyEntry entry, string newName = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            return MapEntry(entry, newName ?? entry.Name, deltas => (Vector3[])deltas.Clone());
        }

        /// <summary>
        /// エントリを指定の重みで評価した差分を、重み 100 の1フレームにして返す（Unity が BlendShape を描くときの計算と同じ）。
        /// 法線・接線は、エントリのどこかのフレームに差分があるときだけ入る（無ければ空）。
        /// </summary>
        public static ShapeKeyFrame EvaluateEntry(ShapeKeyEntry entry, float weight, int vertexCount)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            var frame = new ShapeKeyFrame { Weight = ShapeKeyMath.FullWeight };
            frame.DeltaVertices = EvaluateChannel(entry, weight, vertexCount, f => f.DeltaVertices);
            if (AnyFrame(entry, f => f.HasNormals)) frame.DeltaNormals = EvaluateChannel(entry, weight, vertexCount, f => f.DeltaNormals);
            if (AnyFrame(entry, f => f.HasTangents)) frame.DeltaTangents = EvaluateChannel(entry, weight, vertexCount, f => f.DeltaTangents);
            return frame;
        }

        /// <summary>エントリの差分の長さが頂点数と合っているか（全フレーム・全チャンネル）。</summary>
        public static bool IsEntryCompatible(ShapeKeyEntry entry, int vertexCount)
        {
            if (entry == null) return false;
            foreach (ShapeKeyFrame frame in entry.Frames)
            {
                if (frame == null || frame.DeltaVertices == null || frame.DeltaVertices.Length != vertexCount) return false;
                if (frame.HasNormals && frame.DeltaNormals.Length != vertexCount) return false;
                if (frame.HasTangents && frame.DeltaTangents.Length != vertexCount) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------
        // ボーンウェイトの付け替え
        // ------------------------------------------------------------------

        /// <summary>
        /// 大きく動いた頂点（差分の長さが threshold 以上）のボーンウェイトを、動いた後の位置に最も近い（元の形の）頂点からもらった新しいメッシュを返す。
        /// 裾を短くしたら手のウェイトが残っていて、手首を曲げると壊れる、といった問題を直す簡単な方法。
        /// もらう元のウェイトは、付け替える前のもの（付け替えた結果をさらにもらうことはない）。
        /// </summary>
        /// <exception cref="InvalidOperationException">メッシュにボーンウェイトが無いとき</exception>
        public static Mesh RemapBoneWeights(Mesh mesh, Vector3[] deltas, float threshold)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (deltas == null) throw new ArgumentNullException(nameof(deltas));
            int count = mesh.vertexCount;
            if (deltas.Length != count) throw new ArgumentException($"差分の数（{deltas.Length}）がメッシュの頂点数（{count}）と違います。", nameof(deltas));
            NativeArray<byte> bonesPerVertex = mesh.GetBonesPerVertex();
            if (bonesPerVertex.Length != count) throw new InvalidOperationException($"メッシュ「{mesh.name}」にボーンウェイトがありません。");
            NativeArray<BoneWeight1> allWeights = mesh.GetAllBoneWeights();

            var offsets = new int[count + 1];
            for (int v = 0; v < count; v++) offsets[v + 1] = offsets[v] + bonesPerVertex[v];

            Vector3[] positions = mesh.vertices;
            var grid = new PointGrid(0.02f);
            for (int v = 0; v < count; v++) grid.Add(v, new NVector3(positions[v].x, positions[v].y, positions[v].z));

            // 付け替える頂点ごとの、ウェイトをもらう頂点
            var donor = new int[count];
            var searchResult = new List<int>();
            float thresholdSq = threshold * threshold;
            for (int v = 0; v < count; v++)
            {
                donor[v] = v;
                if (deltas[v].sqrMagnitude < thresholdSq) continue;
                Vector3 moved = positions[v] + deltas[v];
                var target = new NVector3(moved.x, moved.y, moved.z);
                int best = -1;
                float bestDistance = float.MaxValue;
                // 近い頂点が見つかるまで、探す半径を広げる。見つかった最も近い点が半径の内側なら、それが全体で一番近い
                for (float radius = 0.02f; radius <= MaxGridSearchRadius && best < 0; radius *= 2f)
                {
                    grid.Query(target, radius, searchResult);
                    foreach (int u in searchResult)
                    {
                        float d = (positions[u] - moved).sqrMagnitude;
                        if (d < bestDistance && d <= radius * radius)
                        {
                            bestDistance = d;
                            best = u;
                        }
                    }
                }
                if (best < 0)
                {
                    // 遠くに動いた頂点は、格子で探すとセルを調べすぎるので、全部の頂点から探す
                    for (int u = 0; u < count; u++)
                    {
                        float d = (positions[u] - moved).sqrMagnitude;
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            best = u;
                        }
                    }
                }
                donor[v] = best;
            }

            int total = 0;
            for (int v = 0; v < count; v++) total += offsets[donor[v] + 1] - offsets[donor[v]];
            var newBones = new NativeArray<byte>(count, Allocator.Temp);
            var newWeights = new NativeArray<BoneWeight1>(total, Allocator.Temp);
            try
            {
                int write = 0;
                for (int v = 0; v < count; v++)
                {
                    int d = donor[v];
                    newBones[v] = bonesPerVertex[d];
                    for (int k = offsets[d]; k < offsets[d + 1]; k++) newWeights[write++] = allWeights[k];
                }
                Mesh result = Object.Instantiate(mesh);
                result.name = NameWithSuffix(mesh.name);
                result.SetBoneWeights(newBones, newWeights);
                return result;
            }
            finally
            {
                newBones.Dispose();
                newWeights.Dispose();
            }
        }

        // ------------------------------------------------------------------
        // データの作成・保存・Undo
        // ------------------------------------------------------------------

        /// <summary>メッシュ用の編集データのアセットを作る（<see cref="DataFolder"/> など）。</summary>
        public static GP02ShapeKeyData CreateData(Mesh source, string folder = DataFolder)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            folder = folder.TrimEnd('/');
            EnsureFolder(folder);

            var data = ScriptableObject.CreateInstance<GP02ShapeKeyData>();
            data.SourceMesh = source;
            data.VertexCount = source.vertexCount;
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SanitizeFileName(source.name)}_ShapeKeys.asset");
            AssetDatabase.CreateAsset(data, path);
            Undo.RegisterCreatedObjectUndo(data, "GP02 シェイプキーのデータを作成");
            return data;
        }

        /// <summary>データをアセットとして保存する。</summary>
        public static void SaveData(GP02ShapeKeyData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
        }

        /// <summary>エントリをデータに加える（Undo できる）。グループ名がデータの一覧に無ければ、一覧にも加える。</summary>
        public static void AddEntry(GP02ShapeKeyData data, ShapeKeyEntry entry)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            Undo.RecordObject(data, "GP02 シェイプキーを追加");
            data.Entries.Add(entry);
            if (!string.IsNullOrEmpty(entry.Group) && !data.Groups.Contains(entry.Group)) data.Groups.Add(entry.Group);
            EditorUtility.SetDirty(data);
        }

        /// <summary>エントリをデータから外す（Undo できる）。外せたら true。</summary>
        public static bool RemoveEntry(GP02ShapeKeyData data, ShapeKeyEntry entry)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            Undo.RecordObject(data, "GP02 シェイプキーを削除");
            bool removed = data.Entries.Remove(entry);
            if (removed) EditorUtility.SetDirty(data);
            return removed;
        }

        /// <summary>
        /// レンダラーのオブジェクトに <see cref="GP02ShapeKeys"/> を付け（無ければ追加）、データと元のメッシュを設定する（Undo できる）。
        /// </summary>
        public static GP02ShapeKeys Attach(Renderer renderer, GP02ShapeKeyData data)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            if (data == null) throw new ArgumentNullException(nameof(data));
            var component = renderer.GetComponent<GP02ShapeKeys>();
            if (component == null) component = Undo.AddComponent<GP02ShapeKeys>(renderer.gameObject);
            Undo.RecordObject(component, "GP02 シェイプキーのデータを設定");
            component.Data = data;
            component.SourceMesh = data.SourceMesh;
            EditorUtility.SetDirty(component);
            return component;
        }

        /// <summary>
        /// レンダラーのシェイプキーのデータを返す。コンポーネントかデータが無ければ作って付ける（Undo できる）。
        /// データの元のメッシュは、コンポーネントに元のメッシュがあればそれ、無ければレンダラーの今のメッシュ。
        /// メッシュが無ければ null。
        /// </summary>
        public static GP02ShapeKeyData FindOrCreateData(Renderer renderer, string folder = DataFolder)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            var component = renderer.GetComponent<GP02ShapeKeys>();
            if (component != null && component.Data != null) return component.Data;

            Mesh source = component != null && component.SourceMesh != null ? component.SourceMesh : ClothEditController.GetMesh(renderer);
            if (source == null) return null;
            GP02ShapeKeyData data = CreateData(source, folder);
            Attach(renderer, data);
            return data;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
