// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *
 *  This program is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, version 3.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with this program.  If not, see <https://www.gnu.org/licenses/>.
 *
 * NBT, the format Minecraft stores level.dat in. Upstream vendors libnbt++; this is written out
 * because the format is thirteen tag types and no more, and a vendored C++ library is not something
 * that ports.
 *
 * THE LAUNCHER ONLY READS FOUR FIELDS -- the world's name, when it was last played, its game type and
 * its seed -- BUT IT HAS TO WRITE THE WHOLE FILE BACK. Renaming a world means changing one string
 * inside a document full of tags this launcher has never heard of and does not want to understand:
 * player inventories, datapack lists, boss bar state, whatever the next version adds. So the reader
 * cannot skip what it does not recognise; every tag has to survive a round trip byte for byte, or
 * renaming a world quietly destroys it.
 *
 * ═══ TWO PLACES THIS FORMAT IS NOT WHAT IT LOOKS LIKE ═══
 *
 * 1. STRINGS ARE JAVA'S "MODIFIED UTF-8", NOT UTF-8. A NUL is written as the two bytes C0 80 rather
 *    than 00, and characters outside the basic plane are written as a SURROGATE PAIR encoded
 *    separately (CESU-8) rather than as one four-byte sequence. A world named with an emoji round
 *    trips through a plain UTF-8 reader as mojibake -- and emoji in world names are not rare.
 *
 * 2. EVERYTHING IS BIG-ENDIAN, including the string length prefix, on hardware that is not.
 */

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft;

/// <summary>The thirteen NBT tag types, numbered as the format numbers them.</summary>
public enum NbtTagType : byte
{
    End = 0,
    Byte = 1,
    Short = 2,
    Int = 3,
    Long = 4,
    Float = 5,
    Double = 6,
    ByteArray = 7,
    String = 8,
    List = 9,
    Compound = 10,
    IntArray = 11,
    LongArray = 12,
}

/// <summary>One NBT tag. The payload is whatever the type says it is.</summary>
public sealed class NbtTag
{
    public NbtTagType Type { get; set; }

    /// <summary>Set for a tag inside a compound; empty for one inside a list.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The value, typed by <see cref="Type"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately an object rather than a class hierarchy. Thirteen subclasses would be the tidy
    /// shape for a general library; here almost every tag is read once, never inspected, and written
    /// straight back, so what matters is that the payload survives — not that it is convenient to
    /// pattern-match on.
    /// </remarks>
    public object? Value { get; set; }

    /// <summary>For a list: the type of its elements. Meaningless otherwise.</summary>
    public NbtTagType ListType { get; set; }

    public List<NbtTag> Children => (List<NbtTag>)(Value ??= new List<NbtTag>());

    /// <summary>The child of a compound with this name, or null.</summary>
    public NbtTag? Get(string name)
        => Type is NbtTagType.Compound && Value is List<NbtTag> children
            ? children.Find(c => c.Name == name)
            : null;

    /// <summary>The child's value as a string, or null if absent or of another type.</summary>
    public string? GetString(string name)
        => Get(name) is { Type: NbtTagType.String, Value: string value } ? value : null;

    /// <summary>The child's value as a long, or null. Ints are widened, as callers expect.</summary>
    public long? GetLong(string name)
        => Get(name) switch
        {
            { Type: NbtTagType.Long, Value: long value } => value,
            { Type: NbtTagType.Int, Value: int value } => value,
            _ => null,
        };

    public int? GetInt(string name)
        => Get(name) is { Type: NbtTagType.Int, Value: int value } ? value : null;

    /// <summary>Replaces a child's value, or adds the child if it is not there.</summary>
    public void Put(string name, string value)
    {
        if (Get(name) is { } existing)
        {
            existing.Type = NbtTagType.String;
            existing.Value = value;

            return;
        }

        Children.Add(new NbtTag { Type = NbtTagType.String, Name = name, Value = value });
    }
}

public static class Nbt
{
    /// <summary>Reads a gzipped NBT document, as level.dat is stored.</summary>
    public static NbtTag ReadCompressed(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var buffer = new MemoryStream();

        gzip.CopyTo(buffer);

        return Read(buffer.ToArray());
    }

    /// <summary>Writes a gzipped NBT document.</summary>
    public static byte[] WriteCompressed(NbtTag root)
    {
        using var output = new MemoryStream();

        using (var gzip = new GZipStream(output, CompressionMode.Compress))
        {
            var raw = Write(root);

            gzip.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    /// <summary>Reads an uncompressed NBT document.</summary>
    /// <remarks>
    /// A document is a single named tag, in practice always a compound — usually with an empty name.
    /// </remarks>
    public static NbtTag Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var offset = 0;
        var type = (NbtTagType)ReadByte(data, ref offset);

        if (type == NbtTagType.End)
        {
            return new NbtTag { Type = NbtTagType.End };
        }

        var tag = new NbtTag { Type = type, Name = ReadString(data, ref offset) };

        ReadPayload(tag, data, ref offset);

        return tag;
    }

    /// <summary>Writes an uncompressed NBT document.</summary>
    public static byte[] Write(NbtTag root)
    {
        ArgumentNullException.ThrowIfNull(root);

        using var output = new MemoryStream();

        output.WriteByte((byte)root.Type);

        if (root.Type != NbtTagType.End)
        {
            WriteString(output, root.Name);
            WritePayload(output, root);
        }

        return output.ToArray();
    }

    // ================================================================== reading

    private static void ReadPayload(NbtTag tag, byte[] data, ref int offset)
    {
        switch (tag.Type)
        {
            case NbtTagType.Byte:
                tag.Value = unchecked((sbyte)ReadByte(data, ref offset));
                break;

            case NbtTagType.Short:
                tag.Value = BinaryPrimitives.ReadInt16BigEndian(Take(data, ref offset, 2));
                break;

            case NbtTagType.Int:
                tag.Value = BinaryPrimitives.ReadInt32BigEndian(Take(data, ref offset, 4));
                break;

            case NbtTagType.Long:
                tag.Value = BinaryPrimitives.ReadInt64BigEndian(Take(data, ref offset, 8));
                break;

            case NbtTagType.Float:
                tag.Value = BinaryPrimitives.ReadSingleBigEndian(Take(data, ref offset, 4));
                break;

            case NbtTagType.Double:
                tag.Value = BinaryPrimitives.ReadDoubleBigEndian(Take(data, ref offset, 8));
                break;

            case NbtTagType.ByteArray:
                tag.Value = Take(data, ref offset, ReadLength(data, ref offset)).ToArray();
                break;

            case NbtTagType.String:
                tag.Value = ReadString(data, ref offset);
                break;

            case NbtTagType.List:
                ReadList(tag, data, ref offset);
                break;

            case NbtTagType.Compound:
                ReadCompound(tag, data, ref offset);
                break;

            case NbtTagType.IntArray:
                tag.Value = ReadIntArray(data, ref offset);
                break;

            case NbtTagType.LongArray:
                tag.Value = ReadLongArray(data, ref offset);
                break;

            default:
                throw new JsonException($"Unknown NBT tag type {(byte)tag.Type}.");
        }
    }

    private static void ReadList(NbtTag tag, byte[] data, ref int offset)
    {
        tag.ListType = (NbtTagType)ReadByte(data, ref offset);

        var count = ReadLength(data, ref offset);
        var children = new List<NbtTag>(count);

        /*
         * A list's elements are UNNAMED and their type comes from the header rather than from each
         * one. An empty list still carries a type — often End, which is how Minecraft writes an empty
         * list — and that type has to be written back unchanged or the file shape changes.
         */
        for (var i = 0; i < count; i++)
        {
            var child = new NbtTag { Type = tag.ListType };

            if (tag.ListType != NbtTagType.End)
            {
                ReadPayload(child, data, ref offset);
            }

            children.Add(child);
        }

        tag.Value = children;
    }

    private static void ReadCompound(NbtTag tag, byte[] data, ref int offset)
    {
        var children = new List<NbtTag>();

        while (true)
        {
            var type = (NbtTagType)ReadByte(data, ref offset);

            // TAG_End closes the compound and carries no name and no payload.
            if (type == NbtTagType.End)
            {
                break;
            }

            var child = new NbtTag { Type = type, Name = ReadString(data, ref offset) };

            ReadPayload(child, data, ref offset);
            children.Add(child);
        }

        tag.Value = children;
    }

    private static int[] ReadIntArray(byte[] data, ref int offset)
    {
        var count = ReadLength(data, ref offset);
        var values = new int[count];

        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadInt32BigEndian(Take(data, ref offset, 4));
        }

        return values;
    }

    private static long[] ReadLongArray(byte[] data, ref int offset)
    {
        var count = ReadLength(data, ref offset);
        var values = new long[count];

        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadInt64BigEndian(Take(data, ref offset, 8));
        }

        return values;
    }

    private static byte ReadByte(byte[] data, ref int offset)
    {
        if (offset >= data.Length)
        {
            throw new JsonException("Truncated NBT document.");
        }

        return data[offset++];
    }

    /// <remarks>A negative length is refused rather than trusted: it would size an allocation.</remarks>
    private static int ReadLength(byte[] data, ref int offset)
    {
        var count = BinaryPrimitives.ReadInt32BigEndian(Take(data, ref offset, 4));

        if (count < 0 || count > data.Length - offset)
        {
            throw new JsonException($"NBT length {count} does not fit in the document.");
        }

        return count;
    }

    private static ReadOnlySpan<byte> Take(byte[] data, ref int offset, int count)
    {
        if (count < 0 || offset + count > data.Length)
        {
            throw new JsonException("Truncated NBT document.");
        }

        var span = data.AsSpan(offset, count);

        offset += count;

        return span;
    }

    private static string ReadString(byte[] data, ref int offset)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(Take(data, ref offset, 2));

        return DecodeModifiedUtf8(Take(data, ref offset, length));
    }

    // ================================================================== writing

    private static void WritePayload(Stream output, NbtTag tag)
    {
        switch (tag.Type)
        {
            case NbtTagType.Byte:
                output.WriteByte(unchecked((byte)Convert.ToSByte(tag.Value)));
                break;

            case NbtTagType.Short:
                WriteBigEndian(output, 2, (span, v) => BinaryPrimitives.WriteInt16BigEndian(span, (short)v!), tag.Value);
                break;

            case NbtTagType.Int:
                WriteBigEndian(output, 4, (span, v) => BinaryPrimitives.WriteInt32BigEndian(span, (int)v!), tag.Value);
                break;

            case NbtTagType.Long:
                WriteBigEndian(output, 8, (span, v) => BinaryPrimitives.WriteInt64BigEndian(span, (long)v!), tag.Value);
                break;

            case NbtTagType.Float:
                WriteBigEndian(output, 4, (span, v) => BinaryPrimitives.WriteSingleBigEndian(span, (float)v!), tag.Value);
                break;

            case NbtTagType.Double:
                WriteBigEndian(output, 8, (span, v) => BinaryPrimitives.WriteDoubleBigEndian(span, (double)v!), tag.Value);
                break;

            case NbtTagType.ByteArray:
                WriteArrayHeader(output, ((byte[])tag.Value!).Length);
                output.Write((byte[])tag.Value!);
                break;

            case NbtTagType.String:
                WriteString(output, (string)tag.Value!);
                break;

            case NbtTagType.List:
                WriteList(output, tag);
                break;

            case NbtTagType.Compound:
                WriteCompound(output, tag);
                break;

            case NbtTagType.IntArray:
                WriteIntArray(output, (int[])tag.Value!);
                break;

            case NbtTagType.LongArray:
                WriteLongArray(output, (long[])tag.Value!);
                break;

            default:
                throw new JsonException($"Cannot write NBT tag type {(byte)tag.Type}.");
        }
    }

    private static void WriteList(Stream output, NbtTag tag)
    {
        // Null rather than an empty list: a hand-built tag whose children were never touched is a
        // legitimately empty one, not a broken one.
        var children = tag.Value as List<NbtTag> ?? [];

        output.WriteByte((byte)tag.ListType);
        WriteArrayHeader(output, children.Count);

        foreach (var child in children)
        {
            if (tag.ListType != NbtTagType.End)
            {
                WritePayload(output, child);
            }
        }
    }

    private static void WriteCompound(Stream output, NbtTag tag)
    {
        // As in WriteList: an untouched compound is empty, and an empty compound is just TAG_End.
        foreach (var child in tag.Value as List<NbtTag> ?? [])
        {
            output.WriteByte((byte)child.Type);
            WriteString(output, child.Name);
            WritePayload(output, child);
        }

        output.WriteByte((byte)NbtTagType.End);
    }

    private static void WriteIntArray(Stream output, int[] values)
    {
        WriteArrayHeader(output, values.Length);

        foreach (var value in values)
        {
            WriteBigEndian(output, 4, (span, v) => BinaryPrimitives.WriteInt32BigEndian(span, (int)v!), value);
        }
    }

    private static void WriteLongArray(Stream output, long[] values)
    {
        WriteArrayHeader(output, values.Length);

        foreach (var value in values)
        {
            WriteBigEndian(output, 8, (span, v) => BinaryPrimitives.WriteInt64BigEndian(span, (long)v!), value);
        }
    }

    private static void WriteArrayHeader(Stream output, int count)
        => WriteBigEndian(output, 4, (span, v) => BinaryPrimitives.WriteInt32BigEndian(span, (int)v!), count);

    private static void WriteBigEndian(Stream output, int size, Action<Span<byte>, object?> write, object? value)
    {
        Span<byte> buffer = stackalloc byte[size];

        write(buffer, value);
        output.Write(buffer);
    }

    private static void WriteString(Stream output, string value)
    {
        var bytes = EncodeModifiedUtf8(value);

        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)bytes.Length);

        output.Write(length);
        output.Write(bytes);
    }

    // ================================================================== modified UTF-8

    /*
     * JAVA'S ENCODING, NOT UTF-8, and the difference is small enough to miss and large enough to
     * corrupt a world name:
     *
     *   - U+0000 is written as C0 80, so a string can never contain a zero byte.
     *   - Characters above the basic plane are written as their UTF-16 SURROGATE PAIR, each surrogate
     *     encoded as its own three-byte sequence -- six bytes where UTF-8 uses four. This is CESU-8.
     *
     * An emoji in a world name goes through both rules, and a plain UTF-8 reader turns it into two
     * replacement characters.
     */

    /// <summary>Encodes a string the way Java's DataOutput.writeUTF does.</summary>
    public static byte[] EncodeModifiedUtf8(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var output = new List<byte>(value.Length + 8);

        // Iterated by UTF-16 code UNIT, so a surrogate pair is naturally encoded as two sequences.
        foreach (var unit in value)
        {
            if (unit is > '\u0000' and <= '\u007F')
            {
                output.Add((byte)unit);
            }
            else if (unit <= '\u07FF')
            {
                // U+0000 lands here too, which is what produces the C0 80 pair.
                output.Add((byte)(0xC0 | ((unit >> 6) & 0x1F)));
                output.Add((byte)(0x80 | (unit & 0x3F)));
            }
            else
            {
                output.Add((byte)(0xE0 | ((unit >> 12) & 0x0F)));
                output.Add((byte)(0x80 | ((unit >> 6) & 0x3F)));
                output.Add((byte)(0x80 | (unit & 0x3F)));
            }
        }

        return [.. output];
    }

    /// <summary>Decodes a string the way Java's DataInput.readUTF does.</summary>
    public static string DecodeModifiedUtf8(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        var index = 0;

        while (index < bytes.Length)
        {
            var first = bytes[index];

            if ((first & 0x80) == 0)
            {
                builder.Append((char)first);
                index++;
            }
            else if ((first & 0xE0) == 0xC0 && index + 1 < bytes.Length)
            {
                builder.Append((char)(((first & 0x1F) << 6) | (bytes[index + 1] & 0x3F)));
                index += 2;
            }
            else if ((first & 0xF0) == 0xE0 && index + 2 < bytes.Length)
            {
                builder.Append((char)(((first & 0x0F) << 12)
                    | ((bytes[index + 1] & 0x3F) << 6)
                    | (bytes[index + 2] & 0x3F)));

                index += 3;
            }
            else
            {
                /*
                 * Not a sequence this encoding produces. Substituted rather than refused: a corrupt
                 * byte in a world name should not make the world unopenable, and the launcher only
                 * ever displays these.
                 */
                builder.Append('\uFFFD');
                index++;
            }
        }

        return builder.ToString();
    }
}
