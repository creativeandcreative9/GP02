using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>点を格子のセルに分けて、近くの点を素早く探す。</summary>
    public sealed class PointGrid
    {
        // 粒子化では数十万回の Query で数百万回セルを引くので、Dictionary<long, List<int>> では Unity の Mono で遅かった。
        // セルは開番地法のハッシュ表、セルの中の点は配列でつないだリストで持つ。
        // セルの中の点は追加した順に返す（Query の結果の順番を、以前の実装と同じにするため）
        const long EmptyKey = long.MinValue;

        readonly float _cellSize;
        long[] _cellKeys;
        int[] _cellFirst;     // セルの最初の点（_entryNext でつながる）
        int[] _cellLast;      // セルの最後の点（追加した順に並べるため）
        int _cellCount;
        int _mask;
        int[] _entryIndex = new int[16];
        int[] _entryNext = new int[16];
        int _entryCount;

        public PointGrid(float cellSize)
        {
            if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize));
            _cellSize = cellSize;
            Allocate(64);
        }

        void Allocate(int capacity)
        {
            _cellKeys = new long[capacity];
            _cellFirst = new int[capacity];
            _cellLast = new int[capacity];
            for (int i = 0; i < capacity; i++) _cellKeys[i] = EmptyKey;
            _mask = capacity - 1;
            _cellCount = 0;
        }

        public void Add(int index, Vector3 position)
        {
            if (2 * (_cellCount + 1) > _cellKeys.Length) Grow();

            long key = Key(Cell(position.X), Cell(position.Y), Cell(position.Z));
            int slot = FindSlot(key);
            if (_entryCount == _entryIndex.Length)
            {
                Array.Resize(ref _entryIndex, 2 * _entryCount);
                Array.Resize(ref _entryNext, 2 * _entryCount);
            }
            int entry = _entryCount++;
            _entryIndex[entry] = index;
            _entryNext[entry] = -1;

            if (_cellKeys[slot] == EmptyKey)
            {
                _cellKeys[slot] = key;
                _cellFirst[slot] = entry;
                _cellCount++;
            }
            else
            {
                _entryNext[_cellLast[slot]] = entry;
            }
            _cellLast[slot] = entry;
        }

        /// <summary>
        /// position から radius 以内にあるかもしれない点の番号を results に入れる（距離の判定は呼び出し側で行う）。
        /// </summary>
        public void Query(Vector3 position, float radius, List<int> results)
        {
            results.Clear();
            int x0 = Cell(position.X - radius), x1 = Cell(position.X + radius);
            int y0 = Cell(position.Y - radius), y1 = Cell(position.Y + radius);
            int z0 = Cell(position.Z - radius), z1 = Cell(position.Z + radius);
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                int slot = FindSlot(Key(x, y, z));
                if (_cellKeys[slot] == EmptyKey) continue;
                for (int entry = _cellFirst[slot]; entry >= 0; entry = _entryNext[entry]) results.Add(_entryIndex[entry]);
            }
        }

        // key のセルがあればその場所、なければ入れるべき空きの場所
        int FindSlot(long key)
        {
            int slot = (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> 40) & _mask;
            while (true)
            {
                long existing = _cellKeys[slot];
                if (existing == key || existing == EmptyKey) return slot;
                slot = (slot + 1) & _mask;
            }
        }

        void Grow()
        {
            long[] keys = _cellKeys;
            int[] first = _cellFirst, last = _cellLast;
            Allocate(2 * keys.Length);
            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i] == EmptyKey) continue;
                int slot = FindSlot(keys[i]);
                _cellKeys[slot] = keys[i];
                _cellFirst[slot] = first[i];
                _cellLast[slot] = last[i];
                _cellCount++;
            }
        }

        int Cell(float value) => (int)MathF.Floor(value / _cellSize);

        // 各軸21ビットに詰める（1cmのセルなら ±10km まで重ならない）
        static long Key(int x, int y, int z)
        {
            const long Mask = (1L << 21) - 1;
            return ((x & Mask) << 42) | ((y & Mask) << 21) | (z & Mask);
        }
    }
}
