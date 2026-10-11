using System.Collections.Generic;
using GP02.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GP02.Tests
{
    /// <summary>
    /// データを変える操作の前の自動バックアップ（<see cref="ShapeKeyBackup"/>）と、そこから戻す操作を確かめる。
    /// バックアップを取るのは、アセットとして保存されているデータだけ。
    /// </summary>
    public class ShapeKeyBackupTests : ShapeKeyUiTestBase
    {
        const string BackupFolder = TempRoot + "/Backups";

        string _savedFolder;

        [SetUp]
        public void SetUpBackups()
        {
            _savedFolder = ShapeKeyBackup.Folder;
            ShapeKeyBackup.Folder = BackupFolder;
            ShapeKeyBackup.Enabled = true;
        }

        [TearDown]
        public void TearDownBackups()
        {
            ShapeKeyBackup.Folder = _savedFolder;
            ShapeKeyBackup.Enabled = true;
        }

        // メッシュとデータをアセットとして保存して、データを返す
        GP02ShapeKeyData MakeAssetData(out Mesh mesh, params string[] names)
        {
            EnsureFolder(MeshFolder);
            // アセットにしたものは DestroyImmediate できないので、Track していない複製をアセットにする（後始末は TempRoot の削除）
            mesh = Object.Instantiate(CreateGridMesh("BackupGrid"));
            AssetDatabase.CreateAsset(mesh, AssetDatabase.GenerateUniqueAssetPath(MeshFolder + "/BackupGrid.asset"));
            GP02ShapeKeyData data = ShapeKeyLibrary.CreateData(mesh, DataFolder);
            int n = mesh.vertexCount;
            for (int i = 0; i < names.Length; i++) data.Entries.Add(Entry(names[i], Ramp(n, 0.002f * (i + 1))));
            return data;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
        }

        [Test]
        public void Delete_TakesABackupOfTheStateBeforeAndRestoreBringsItBack()
        {
            GP02ShapeKeyData data = MakeAssetData(out _, "A", "B", "C");
            Assert.That(ShapeKeyBackup.List(data), Is.Empty);

            NextUndoGroup();
            ShapeKeyListOperations.Delete(data, new[] { data.Entries[0], data.Entries[2] });
            NextUndoGroup();

            List<ShapeKeyBackupInfo> backups = ShapeKeyBackup.List(data);
            Assert.That(backups.Count, Is.EqualTo(1));
            Assert.That(backups[0].Path, Does.StartWith(BackupFolder + "/"));
            Assert.That(backups[0].Reason, Is.EqualTo("Delete"));
            var saved = AssetDatabase.LoadAssetAtPath<GP02ShapeKeyData>(backups[0].Path);
            Assert.That(saved.Entries.Count, Is.EqualTo(3), "消す前の状態");
            Assert.That(data.Entries.Count, Is.EqualTo(1));

            Assert.That(ShapeKeyListOperations.RestoreFromBackup(data, backups[0].Path), Is.True);
            NextUndoGroup();
            Assert.That(data.Entries.ConvertAll(e => e.Name), Is.EqualTo(new[] { "A", "B", "C" }));
            AssertSame(Ramp(data.VertexCount, 0.006f), data.Entries[2].Frames[0].DeltaVertices, 1e-7f);
            Assert.That(data.Entries[0], Is.Not.SameAs(saved.Entries[0]), "バックアップとは別のコピー");
            Assert.That(ShapeKeyBackup.List(data)[0].Reason, Is.EqualTo("before_restore"), "戻す前の状態もバックアップに残る");

            Undo.PerformUndo();
            Assert.That(data.Entries.ConvertAll(e => e.Name), Is.EqualTo(new[] { "B" }), "戻した操作も Ctrl+Z で戻せる");
            Assert.That(ShapeKeyListOperations.RestoreFromBackup(data, "Assets/Nothing.asset"), Is.False);
        }

        [Test]
        public void OnlyTheLatestFiveBackupsArePerData_AndOtherDataKeepTheirOwn()
        {
            GP02ShapeKeyData data = MakeAssetData(out _, "A");
            GP02ShapeKeyData other = MakeAssetData(out _, "X");

            for (int i = 0; i < 7; i++)
            {
                ShapeKeyListOperations.Scale(data, new[] { data.Entries[0] }, 1.1f);
                NextUndoGroup();
            }
            ShapeKeyListOperations.Scale(other, new[] { other.Entries[0] }, 2f);

            List<ShapeKeyBackupInfo> backups = ShapeKeyBackup.List(data);
            Assert.That(backups.Count, Is.EqualTo(ShapeKeyBackup.MaxPerData));
            Assert.That(ShapeKeyBackup.List(other).Count, Is.EqualTo(1));
            for (int i = 1; i < backups.Count; i++) Assert.That(backups[i - 1].Time, Is.GreaterThan(backups[i].Time), "新しい順");
            // 一番古く残っているのは 3 回目の操作の前（1・2 回目の分は消えた）
            var oldest = AssetDatabase.LoadAssetAtPath<GP02ShapeKeyData>(backups[backups.Count - 1].Path);
            Assert.That(oldest.Entries[0].Frames[0].DeltaVertices[1].z, Is.EqualTo(Ramp(data.VertexCount, 0.002f)[1].z * Mathf.Pow(1.1f, 2)).Within(1e-6f));
        }

        [Test]
        public void EveryDestructiveOperationTakesABackup_ButAdditiveOnesDoNot()
        {
            GP02ShapeKeyData data = MakeAssetData(out Mesh mesh, "A", "B", "C", "D");
            int n = mesh.vertexCount;
            ShapeKeyEntry a = data.Entries[0], b = data.Entries[1], c = data.Entries[2];

            // 足すだけの操作では取らない
            ShapeKeyListOperations.Duplicate(data, new[] { a });
            ShapeKeyListOperations.Rename(data, b, "B2");
            ShapeKeyListOperations.SetGroup(data, new[] { a }, "G");
            ShapeKeyListOperations.Subtract(data, a, b);
            ShapeKeyListOperations.SplitLeftRight(data, c, 0, Size / 2f, 0.04f, true, false, out _, out _);
            Assert.That(ShapeKeyBackup.List(data), Is.Empty);

            var reasons = new List<string>();
            void Check(string reason)
            {
                List<ShapeKeyBackupInfo> list = ShapeKeyBackup.List(data);
                Assert.That(list.Count, Is.GreaterThan(0), reason);
                Assert.That(list[0].Reason, Is.EqualTo(reason));
                reasons.Add(reason);
            }

            ShapeKeyListOperations.Merge(data, new[] { data.Entries[0], data.Entries[1] }, null, "M", true);
            Check("Merge");
            ShapeKeyEntry target = data.Entries.Find(e => e.Name == "D");
            ShapeKeyListOperations.SplitLeftRight(data, target, 0, Size / 2f, 0.04f, true, true, out _, out _);
            Check("Split_left_right");
            ShapeKeyListOperations.Flip(data, data.Entries.Find(e => e.Name == "D_L"), 0, Size / 2f, 1e-4f, false);
            Check("Flip");
            ShapeKeyListOperations.Symmetrize(data, data.Entries.Find(e => e.Name == "D_R"), 0, Size / 2f, 1e-4f, true, false);
            Check("Symmetrize");
            ShapeKeyListOperations.RecomputeNormals(data, new[] { data.Entries.Find(e => e.Name == "D_R") }, 180f, true);
            Check("Recompute_normals");
            ShapeKeyEntry two = Entry("Two", Ramp(n, 0.002f), 40f);
            two.Frames.Add(new ShapeKeyFrame { Weight = 100f, DeltaVertices = Ramp(n, 0.004f) });
            data.Entries.Add(two);
            ShapeKeyListOperations.DeleteFrame(data, two, 0);
            Check("Delete_frame");
            ShapeKeyListOperations.RestoreVersion(data, data.Entries.Find(e => e.Name == "Two"), 0);
            Check("Restore_older_version");
            ShapeKeyListOperations.MakeInBetween(data, new[] { data.Entries.Find(e => e.Name == "M"), data.Entries.Find(e => e.Name == "Two") }, "Chain", ShapeKeyInBetweenMode.Crossfade, null, true);
            Check("Create_in_between");
            ShapeKeyListOperations.Delete(data, new[] { data.Entries.Find(e => e.Name == "Chain") });
            Check("Delete");

            float[] mask = new float[n];
            for (int i = 0; i < n; i++) mask[i] = i % 2;
            ShapeKeyListOperations.ApplyMask(data, data.Entries[0], mask, 0, ShapeKeyMaskOperation.Remove, false);
            Check("Remove_the_selected_range");
            Assert.That(reasons.Count, Is.GreaterThanOrEqualTo(9));
        }

        [Test]
        public void DataThatIsNotAnAsset_OrWhenDisabled_GetsNoBackupAndNothingBreaks()
        {
            Mesh mesh = CreateGridMesh();
            GP02ShapeKeyData memory = NewData(mesh, Entry("A", Ramp(mesh.vertexCount, 0.002f)));
            Assert.That(ShapeKeyBackup.Create(memory, "Delete"), Is.Null);
            ShapeKeyListOperations.Delete(memory, new[] { memory.Entries[0] });
            Assert.That(ShapeKeyBackup.List(memory), Is.Empty);
            Assert.That(AssetDatabase.IsValidFolder(BackupFolder), Is.False);

            GP02ShapeKeyData asset = MakeAssetData(out _, "A");
            ShapeKeyBackup.Enabled = false;
            Assert.That(ShapeKeyBackup.Create(asset, "Delete"), Is.Null);
            ShapeKeyListOperations.Delete(asset, new[] { asset.Entries[0] });
            Assert.That(ShapeKeyBackup.List(asset), Is.Empty);
        }

        [Test]
        public void ClothEditStyleOverwrite_KeepsBothABackupAndAHistoryVersion()
        {
            GP02ShapeKeyData data = MakeAssetData(out Mesh mesh, "A");
            int n = mesh.vertexCount;
            var fresh = new List<ShapeKeyFrame> { new ShapeKeyFrame { Weight = 100f, DeltaVertices = Ramp(n, 0.009f) } };

            ShapeKeyBackup.Create(data, "Overwrite");
            ShapeKeyHistory.Overwrite(data.Entries[0], fresh, new int[0], "布で編集して上書き");

            Assert.That(ShapeKeyBackup.List(data)[0].Reason, Is.EqualTo("Overwrite"));
            Assert.That(data.Entries[0].History.Count, Is.EqualTo(1));
            Assert.That(ShapeKeyBackupGUI.DescribeReason("Overwrite"), Is.Not.EqualTo("Overwrite"));
            Assert.That(ShapeKeyBackupGUI.DescribeReason("something_new"), Is.EqualTo("something_new"));
        }
    }
}
