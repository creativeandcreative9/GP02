using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace GP02.EditorTools
{
    /// <summary>
    /// 編集中の状態の「取っておき」。スクリプトのコンパイル・プレイモード・ウィンドウを閉じるときに、保存していない編集が黙って消えないよう、
    /// 編集中の状態（対象のレンダラー、メッシュの頂点数、粒子の位置、ピン、選択、日時）を <c>Library/GP02/EditStash/</c> に書き出す。
    /// 戻るときは、同じ対象であることを確かめてから編集を始め直し、位置・ピン・選択を戻す。
    /// 粒子の置き方は、同じ入力（メッシュ・ポーズ）なら同じになる（テストで確かめている）ので、粒子の数と元の位置の標本が合えば、位置をそのまま戻せる。
    /// 合わないとき（衣装・メッシュ・ポーズが変わった）は、戻さずに知らせる。
    /// 画面に依存しないので、書き出し・読み込み・照合はテストで確かめられる。
    /// </summary>
    public static class ClothEditStash
    {
        public const int FormatVersion = 1;
        public const string FileExtension = ".gp02stash";

        /// <summary>粒子の元の位置が、この距離（メートル）以内なら同じとみなす。</summary>
        public const float RestTolerance = 0.001f;

        /// <summary>照合のために取っておく、粒子の元の位置の標本の数の上限。</summary>
        public const int MaxRestSamples = 1024;

        /// <summary>これより古い取っておきは、ウィンドウを開いたときに消す。</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

        const int Magic = 0x32305047;   // "GP02"

        static string _folderOverride;

        /// <summary>取っておきを置くフォルダ。既定は プロジェクトの Library/GP02/EditStash。テストでは別のフォルダに差し替える。</summary>
        public static string Folder
        {
            get => _folderOverride ?? DefaultFolder;
            set => _folderOverride = value;
        }

        public static string DefaultFolder
        {
            get
            {
                string project = Path.GetDirectoryName(Application.dataPath) ?? ".";
                return Path.Combine(project, "Library", "GP02", "EditStash");
            }
        }

        /// <summary>取っておいた編集の内容（ヘッダーだけ読んだときは、<see cref="HasBody"/> が false で、位置などは空）。</summary>
        public sealed class Record
        {
            /// <summary>対象のレンダラーの組を表す名前（ファイル名になる）。</summary>
            public string Key = "";

            public long SavedTicksUtc;

            /// <summary>衣装の名前（画面に出す）。</summary>
            public string DisplayName = "";

            /// <summary>レンダラーごとの GlobalObjectId の文字列（保存していないシーンなどで分からなければ空）。</summary>
            public string[] RendererIds = new string[0];

            /// <summary>レンダラーごとのシーンの階層パス（ID が使えないときの照合に使う）。</summary>
            public string[] RendererPaths = new string[0];

            public string[] MeshNames = new string[0];

            /// <summary>レンダラーごとのメッシュの頂点数。</summary>
            public int[] VertexCounts = new int[0];

            public int ParticleCount;

            /// <summary>元の位置の標本を、粒子 RestStride 個ごとに1つ取っている。</summary>
            public int RestStride = 1;

            /// <summary>粒子の元の位置の標本（x, y, z, x, y, z, …）。</summary>
            public float[] RestSamples = new float[0];

            public bool HasBody;

            /// <summary>粒子の位置（ワールド、x, y, z, …）。</summary>
            public float[] Positions = new float[0];

            public int[] PinnedParticles = new int[0];
            public int[] SelectedParticles = new int[0];

            public DateTime SavedAtUtc => new DateTime(SavedTicksUtc, DateTimeKind.Utc);
            public int RendererCount => RendererIds.Length;
        }

        // ---- 対象の識別 ----

        /// <summary>
        /// オブジェクトの GlobalObjectId の文字列。保存していないシーンのオブジェクトなど、識別できないときは空。
        /// プレイモードやスクリプトの再読み込みをまたいでも、保存したシーンのオブジェクトなら同じ値になる。
        /// </summary>
        public static string IdOf(Object obj)
        {
            if (obj == null) return "";
            try
            {
                GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(obj);
                return id.identifierType == 0 ? "" : id.ToString();
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>シーンの階層パス（兄弟の順番つき）。ID が使えないときの照合に使う。</summary>
        public static string PathOf(Component component)
        {
            if (component == null) return "";
            var parts = new List<string>();
            for (Transform t = component.transform; t != null; t = t.parent) parts.Add(t.name + "#" + t.GetSiblingIndex());
            parts.Reverse();
            Scene scene = component.gameObject.scene;
            return (scene.IsValid() ? scene.path : "") + "|" + string.Join("/", parts);
        }

        /// <summary>レンダラーの組を表す名前。並び順に関係なく、同じ組なら同じ名前になる。</summary>
        public static string KeyFor(IReadOnlyList<Renderer> renderers)
        {
            var names = new List<string>();
            if (renderers != null)
            {
                foreach (Renderer renderer in renderers)
                {
                    if (renderer == null) continue;
                    string id = IdOf(renderer);
                    names.Add(id.Length > 0 ? id : PathOf(renderer));
                }
            }
            names.Sort(StringComparer.Ordinal);
            return Hash(string.Join(";", names));
        }

        // FNV-1a の 64 ビット。ファイル名に使うので、記号を含まない 16 桁の文字列にする
        static string Hash(string text)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (char c in text)
                {
                    hash ^= c;
                    hash *= 1099511628211UL;
                }
                return hash.ToString("x16");
            }
        }

        public static string PathForKey(string key) => Path.Combine(Folder, key + FileExtension);

        // ---- 取っておく ----

        /// <summary>
        /// 今の編集を取っておく形にする。保存していない編集（形の変更）・ピン・選んだ点のどれも無ければ、取っておく価値が無いので null。
        /// 編集中でなければ null。
        /// </summary>
        public static Record Capture(ClothEditController controller)
        {
            if (controller == null || !controller.IsEditing) return null;
            ClothEditSession session = controller.Session;
            if (!(controller.HasUnsavedEdits || session.PinnedCount > 0 || controller.SelectionCount > 0)) return null;

            IReadOnlyList<Renderer> renderers = session.Renderers;
            int count = renderers.Count;
            var record = new Record
            {
                Key = KeyFor(renderers),
                SavedTicksUtc = DateTime.UtcNow.Ticks,
                RendererIds = new string[count],
                RendererPaths = new string[count],
                MeshNames = new string[count],
                VertexCounts = new int[count],
                ParticleCount = session.ParticleCount,
                HasBody = true,
            };
            var names = new string[count];
            for (int r = 0; r < count; r++)
            {
                Renderer renderer = renderers[r];
                names[r] = renderer != null ? renderer.name : "";
                record.RendererIds[r] = IdOf(renderer);
                record.RendererPaths[r] = PathOf(renderer);
                record.MeshNames[r] = session.GetSourceMesh(r) != null ? session.GetSourceMesh(r).name : "";
                session.GetVertexRange(r, out _, out int vertexCount);
                record.VertexCounts[r] = vertexCount;
            }
            record.DisplayName = string.Join(", ", names);

            NVector3[] rest = session.Cloth.Model.RestPositions;
            record.RestStride = Math.Max(1, (rest.Length + MaxRestSamples - 1) / MaxRestSamples);
            int samples = (rest.Length + record.RestStride - 1) / record.RestStride;
            record.RestSamples = new float[3 * samples];
            for (int k = 0; k < samples; k++)
            {
                NVector3 p = rest[k * record.RestStride];
                record.RestSamples[3 * k] = p.X;
                record.RestSamples[3 * k + 1] = p.Y;
                record.RestSamples[3 * k + 2] = p.Z;
            }

            ReadOnlySpan<NVector3> positions = session.Solver.Positions;
            record.Positions = new float[3 * positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                record.Positions[3 * i] = positions[i].X;
                record.Positions[3 * i + 1] = positions[i].Y;
                record.Positions[3 * i + 2] = positions[i].Z;
            }
            var pinned = new List<int>();
            session.GetPinnedParticles(pinned);
            record.PinnedParticles = pinned.ToArray();
            IReadOnlyList<int> selected = controller.SelectedParticles;
            record.SelectedParticles = new int[selected.Count];
            for (int k = 0; k < selected.Count; k++) record.SelectedParticles[k] = selected[k];
            return record;
        }

        /// <summary>取っておきをファイルに書く。書けたら true。書いている途中で止まっても古いファイルを壊さないよう、別のファイルに書いてから置き換える。</summary>
        public static bool Write(Record record)
        {
            if (record == null || string.IsNullOrEmpty(record.Key)) return false;
            try
            {
                Directory.CreateDirectory(Folder);
                string path = PathForKey(record.Key);
                string temp = path + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(Magic);
                    writer.Write(FormatVersion);
                    writer.Write(record.SavedTicksUtc);
                    writer.Write(record.Key);
                    writer.Write(record.DisplayName ?? "");
                    writer.Write(record.RendererIds.Length);
                    for (int r = 0; r < record.RendererIds.Length; r++)
                    {
                        writer.Write(record.RendererIds[r] ?? "");
                        writer.Write(record.RendererPaths[r] ?? "");
                        writer.Write(record.MeshNames[r] ?? "");
                        writer.Write(record.VertexCounts[r]);
                    }
                    writer.Write(record.ParticleCount);
                    writer.Write(record.RestStride);
                    WriteFloats(writer, record.RestSamples);
                    WriteFloats(writer, record.Positions);
                    WriteInts(writer, record.PinnedParticles);
                    WriteInts(writer, record.SelectedParticles);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[GP02] " + L10n.Tr("編集の取っておきを書けませんでした: ", "Could not write the edit stash: ") + ex.Message);
                return false;
            }
        }

        /// <summary>ファイルから読む。headerOnly なら、位置・ピン・選択は読まない（一覧や帯の表示に使う軽い読み方）。</summary>
        public static bool TryRead(string path, bool headerOnly, out Record record)
        {
            record = null;
            try
            {
                if (!File.Exists(path)) return false;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion) return false;
                    var result = new Record
                    {
                        SavedTicksUtc = reader.ReadInt64(),
                        Key = reader.ReadString(),
                        DisplayName = reader.ReadString(),
                    };
                    int count = reader.ReadInt32();
                    if (count < 0 || count > 1024) return false;
                    result.RendererIds = new string[count];
                    result.RendererPaths = new string[count];
                    result.MeshNames = new string[count];
                    result.VertexCounts = new int[count];
                    for (int r = 0; r < count; r++)
                    {
                        result.RendererIds[r] = reader.ReadString();
                        result.RendererPaths[r] = reader.ReadString();
                        result.MeshNames[r] = reader.ReadString();
                        result.VertexCounts[r] = reader.ReadInt32();
                    }
                    result.ParticleCount = reader.ReadInt32();
                    result.RestStride = reader.ReadInt32();
                    result.RestSamples = ReadFloats(reader);
                    if (!headerOnly)
                    {
                        result.Positions = ReadFloats(reader);
                        result.PinnedParticles = ReadInts(reader);
                        result.SelectedParticles = ReadInts(reader);
                        result.HasBody = true;
                    }
                    record = result;
                    return true;
                }
            }
            catch (Exception)
            {
                // 途中で切れた・壊れたファイルは、無いものとして扱う
                record = null;
                return false;
            }
        }

        static void WriteFloats(BinaryWriter writer, float[] values)
        {
            writer.Write(values.Length);
            var bytes = new byte[values.Length * sizeof(float)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            writer.Write(bytes);
        }

        static void WriteInts(BinaryWriter writer, int[] values)
        {
            writer.Write(values.Length);
            var bytes = new byte[values.Length * sizeof(int)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            writer.Write(bytes);
        }

        static float[] ReadFloats(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 100000000) throw new InvalidDataException();
            byte[] bytes = reader.ReadBytes(length * sizeof(float));
            if (bytes.Length != length * sizeof(float)) throw new EndOfStreamException();
            var values = new float[length];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }

        static int[] ReadInts(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 100000000) throw new InvalidDataException();
            byte[] bytes = reader.ReadBytes(length * sizeof(int));
            if (bytes.Length != length * sizeof(int)) throw new EndOfStreamException();
            var values = new int[length];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }

        // ---- 探す・照合する ----

        /// <summary>
        /// 今の対象（レンダラーの組）に合う取っておきを探す（ヘッダーだけ読む）。別の衣装のもの・頂点数が違うものは見つけない。
        /// </summary>
        public static bool TryFind(IReadOnlyList<Renderer> targets, out Record header)
        {
            header = null;
            if (targets == null || targets.Count == 0) return false;
            if (!TryRead(PathForKey(KeyFor(targets)), true, out Record record)) return false;
            if (!Matches(record, targets, out _)) return false;
            header = record;
            return true;
        }

        /// <summary>
        /// 取っておきの対象が、今の対象と同じか。衣装（GlobalObjectId、無ければシーンの階層パス）と、メッシュの頂点数が合うこと。
        /// 合わなければ false で、理由（利用者向け）を reason に入れる。
        /// </summary>
        public static bool Matches(Record record, IReadOnlyList<Renderer> targets, out string reason)
        {
            reason = null;
            if (record == null || targets == null || targets.Count == 0)
            {
                reason = L10n.Tr("対象がありません。", "There is no target.");
                return false;
            }
            if (record.RendererIds.Length != targets.Count)
            {
                reason = L10n.Tr("前回と衣装の数が違います。", "The number of outfits differs from last time.");
                return false;
            }
            var used = new bool[targets.Count];
            for (int i = 0; i < record.RendererIds.Length; i++)
            {
                int found = -1;
                for (int j = 0; j < targets.Count && found < 0; j++)
                {
                    if (!used[j] && targets[j] != null && IsSameRenderer(record, i, targets[j])) found = j;
                }
                if (found < 0)
                {
                    reason = L10n.Tr("前回とは別の衣装です。", "This is a different outfit from last time.");
                    return false;
                }
                used[found] = true;
                Mesh mesh = ClothEditController.GetMesh(targets[found]);
                if (mesh == null || mesh.vertexCount != record.VertexCounts[i])
                {
                    reason = L10n.Tr($"メッシュの頂点数が前回と違います（前回 {record.VertexCounts[i]:N0}、今 {(mesh != null ? mesh.vertexCount : 0):N0}）。",
                        $"The mesh vertex count differs from last time (then {record.VertexCounts[i]:N0}, now {(mesh != null ? mesh.vertexCount : 0):N0}).");
                    return false;
                }
            }
            return true;
        }

        static bool IsSameRenderer(Record record, int index, Renderer renderer)
        {
            string id = IdOf(renderer);
            if (id.Length > 0 && !string.IsNullOrEmpty(record.RendererIds[index])) return id == record.RendererIds[index];
            return PathOf(renderer) == record.RendererPaths[index];
        }

        /// <summary>
        /// 編集を始め直したセッションが、取っておいたときのものと同じ布か。粒子の数・頂点数と、粒子の元の位置の標本を見る
        /// （衣装・メッシュ・ポーズが変わっていれば合わない）。合わなければ false で、理由（利用者向け）を reason に入れる。
        /// </summary>
        public static bool MatchesSession(Record record, ClothEditSession session, out string reason)
        {
            reason = null;
            if (record == null || session == null)
            {
                reason = L10n.Tr("編集中ではありません。", "You are not editing.");
                return false;
            }
            if (record.RendererIds.Length != session.RendererCount)
            {
                reason = L10n.Tr("前回と衣装の数が違います。", "The number of outfits differs from last time.");
                return false;
            }
            for (int r = 0; r < session.RendererCount; r++)
            {
                session.GetVertexRange(r, out _, out int vertexCount);
                if (vertexCount != record.VertexCounts[r])
                {
                    reason = L10n.Tr("メッシュの頂点数が前回と違います。", "The mesh vertex count differs from last time.");
                    return false;
                }
            }
            if (record.ParticleCount != session.ParticleCount)
            {
                reason = L10n.Tr($"粒子の数が前回と違います（前回 {record.ParticleCount:N0}、今 {session.ParticleCount:N0}）。衣装の形が変わったようです。",
                    $"The particle count differs from last time (then {record.ParticleCount:N0}, now {session.ParticleCount:N0}). The outfit seems to have changed.");
                return false;
            }
            NVector3[] rest = session.Cloth.Model.RestPositions;
            int stride = Math.Max(1, record.RestStride);
            int samples = (rest.Length + stride - 1) / stride;
            if (record.RestSamples.Length != 3 * samples)
            {
                reason = L10n.Tr("前回の記録を照合できません。", "The previous record cannot be checked.");
                return false;
            }
            float limitSq = RestTolerance * RestTolerance;
            for (int k = 0; k < samples; k++)
            {
                NVector3 p = rest[k * stride];
                float dx = p.X - record.RestSamples[3 * k], dy = p.Y - record.RestSamples[3 * k + 1], dz = p.Z - record.RestSamples[3 * k + 2];
                if (dx * dx + dy * dy + dz * dz > limitSq)
                {
                    reason = L10n.Tr("衣装の位置か形が前回と違います（アバターを動かした、ポーズを変えた、シェイプキーの重みを変えた、など）。元に戻してからもう一度試してください。",
                        "The outfit's position or shape differs from last time (the avatar moved, the pose changed, or shape key weights changed). Put it back and try again.");
                    return false;
                }
            }
            return true;
        }

        // ---- 後始末 ----

        /// <summary>対象の取っておきを消す。</summary>
        public static void DeleteFor(IReadOnlyList<Renderer> targets)
        {
            if (targets == null || targets.Count == 0) return;
            DeleteKey(KeyFor(targets));
        }

        public static void DeleteKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            try
            {
                string path = PathForKey(key);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // 消せなくても、編集には関係ない
            }
        }

        /// <summary>maxAge より古い取っておきを消す。消した数を返す。</summary>
        public static int DeleteOlderThan(TimeSpan maxAge)
        {
            int deleted = 0;
            try
            {
                if (!Directory.Exists(Folder)) return 0;
                foreach (string path in Directory.GetFiles(Folder, "*" + FileExtension))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= maxAge) continue;
                    File.Delete(path);
                    deleted++;
                }
            }
            catch (Exception)
            {
                // 消せなくても、編集には関係ない
            }
            return deleted;
        }

        /// <summary>取っておきのファイルの数。</summary>
        public static int Count()
        {
            return Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*" + FileExtension).Length : 0;
        }

        /// <summary>
        /// 一番新しい取っておきの対象を探して、シーンの中のレンダラーに戻す（ウィンドウを開き直したときに、前回の衣装を対象にするため）。
        /// 7 日より古いもの・見つからないもの・頂点数が合わないものは使わない。
        /// </summary>
        public static bool TryResolveLatestTargets(out List<Renderer> targets, out Record header)
        {
            targets = null;
            header = null;
            try
            {
                if (!Directory.Exists(Folder)) return false;
                var files = new List<string>(Directory.GetFiles(Folder, "*" + FileExtension));
                files.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                foreach (string path in files)
                {
                    if (!TryRead(path, true, out Record record)) continue;
                    if (DateTime.UtcNow - record.SavedAtUtc > MaxAge) continue;
                    if (!TryResolve(record, out List<Renderer> found)) continue;
                    if (!Matches(record, found, out _)) continue;
                    targets = found;
                    header = record;
                    return true;
                }
            }
            catch (Exception)
            {
                // 見つけられなくても、編集には関係ない
            }
            return false;
        }

        // 取っておきのレンダラーを、GlobalObjectId（無ければ階層パス）でシーンから探す。1つでも見つからなければ false
        static bool TryResolve(Record record, out List<Renderer> found)
        {
            found = new List<Renderer>();
            Renderer[] all = null;
            for (int i = 0; i < record.RendererIds.Length; i++)
            {
                Renderer renderer = null;
                if (!string.IsNullOrEmpty(record.RendererIds[i]) && GlobalObjectId.TryParse(record.RendererIds[i], out GlobalObjectId id))
                {
                    renderer = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as Renderer;
                }
                if (renderer == null)
                {
                    if (all == null) all = Object.FindObjectsOfType<Renderer>();
                    foreach (Renderer candidate in all)
                    {
                        if (candidate.gameObject.scene.IsValid() && PathOf(candidate) == record.RendererPaths[i])
                        {
                            renderer = candidate;
                            break;
                        }
                    }
                }
                if (renderer == null || found.Contains(renderer)) return false;
                found.Add(renderer);
            }
            return found.Count > 0;
        }

        // ---- 画面に出す文字 ----

        /// <summary>「たった今」「3分前」「2時間前」「1日前」。</summary>
        public static string DescribeAge(TimeSpan age)
        {
            if (age < TimeSpan.FromSeconds(45)) return L10n.Tr("たった今", "just now");
            if (age < TimeSpan.FromHours(1))
            {
                int minutes = Math.Max(1, (int)age.TotalMinutes);
                return L10n.Tr($"{minutes}分前", $"{minutes} min ago");
            }
            if (age < TimeSpan.FromDays(1))
            {
                int hours = (int)age.TotalHours;
                return L10n.Tr($"{hours}時間前", $"{hours} h ago");
            }
            int days = (int)age.TotalDays;
            return L10n.Tr($"{days}日前", $"{days} d ago");
        }

        /// <summary>帯に出す、衣装の名前と経過時間（「Skirt (3分前)」）。区切りは、フォントによっては出ない文字を避けて、ASCII にしてある。</summary>
        public static string Describe(Record record, DateTime nowUtc)
        {
            return record.DisplayName + " (" + DescribeAge(nowUtc - record.SavedAtUtc) + ")";
        }
    }
}
