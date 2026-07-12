namespace Eidosc.Utils
{
    public readonly struct SourceLocation(int position, int line, int column, string? filePath = null)
    {
        public readonly int Position = position;

        /// <summary>Source line number, 0-based.</summary>
        public readonly int Line = line;

        /// <summary>Source column number, 0-based.</summary>
        public readonly int Column = column;

        /// <summary>Absolute or logical source file path.</summary>
        public readonly string? FilePath = filePath;

        public override string ToString()
        {
            return string.Format("({0}, {1})", Line + 1, Column + 1);
        }

        public static int Compare(SourceLocation x, SourceLocation y)
        {
            if (x.Position < y.Position) return -1;
            if (x.Position == y.Position) return 0;
            return 1;
        }

        public static SourceLocation Empty { get; } = new();

        public static SourceLocation operator +(SourceLocation x, SourceLocation y)
        {
            return new SourceLocation(
                x.Position + y.Position,
                x.Line + y.Line,
                x.Column + y.Column,
                x.FilePath ?? y.FilePath);
        }

        public static SourceLocation operator +(SourceLocation x, int offset)
        {
            return new SourceLocation(x.Position + offset, x.Line, x.Column + offset, x.FilePath);
        }
    }

    public readonly struct SourceSpan(SourceLocation location, int length)
    {
        public readonly SourceLocation Location = location;
        public readonly int Length = length;

        /// <summary>
        /// 起始位置（便捷属性）
        /// </summary>
        public int Position => Location.Position;

        public string? FilePath => Location.FilePath;

        public int EndPosition => Location.Position + Length;

        /// <summary>
        /// 空的 SourceSpan
        /// </summary>
        public static SourceSpan Empty { get; } = new(SourceLocation.Empty, 0);

        public bool InRange(int position)
        {
            return position >= Location.Position && position <= EndPosition;
        }
    }
}
