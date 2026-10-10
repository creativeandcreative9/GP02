using System;
using System.Collections.Generic;
using System.Numerics;

namespace GP02.Core
{
    /// <summary>点を格子のセルに分けて、近くの点を素早く探す。</summary>
    internal sealed class PointGrid
    {
        readonly float _cellSize;
        readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>(PairKeys.Comparer);

        public PointGrid(float cellSize)
        {
            if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize));
            _cellSize = cellSize;
        }

        public void Add(int index, Vector3 position)
        {
            long key = Key(Cell(position.X), Cell(position.Y), Cell(position.Z));
            if (!_cells.TryGetValue(key, out var list))
            {
                list = new List<int>(4);
                _cells.Add(key, list);
            }
            list.Add(index);
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
                if (_cells.TryGetValue(Key(x, y, z), out var list)) results.AddRange(list);
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
