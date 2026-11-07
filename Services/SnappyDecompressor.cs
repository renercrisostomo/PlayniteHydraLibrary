using System;

namespace HydraPlaynite.Services
{
    public static class SnappyDecompressor
    {
        private const int MaxUncompressedBlockSizeBytes = 64 * 1024 * 1024;
        private const int TagLiteral = 0;
        private const int TagCopy1ByteOffset = 1;
        private const int TagCopy2ByteOffset = 2;

        public static byte[] Decompress(byte[] input, int offset, int length)
        {
            try
            {
                int pos = offset;
                int end = offset + length;
                int uncompressedLength = ReadVarint32(input, ref pos, end);
                if (uncompressedLength <= 0 || uncompressedLength > MaxUncompressedBlockSizeBytes)
                    return null;

                byte[] output = new byte[uncompressedLength];
                int outPos = 0;

                while (pos < end && outPos < uncompressedLength)
                {
                    byte tag = input[pos++];
                    int tagType = tag & 0x03;

                    if (tagType == TagLiteral)
                    {
                        int literalLength = ReadLiteralLength(input, tag, ref pos, end);
                        if (literalLength < 0 || pos + literalLength > end || outPos + literalLength > uncompressedLength)
                            break;

                        Buffer.BlockCopy(input, pos, output, outPos, literalLength);
                        pos += literalLength;
                        outPos += literalLength;
                    }
                    else
                    {
                        int copyLength;
                        int copyOffset;
                        if (!TryDecodeCopyInstruction(input, tag, tagType, ref pos, end, out copyLength, out copyOffset))
                            break;

                        if (copyOffset <= 0 || outPos - copyOffset < 0)
                            break;

                        for (int i = 0; i < copyLength && outPos < uncompressedLength; i++)
                        {
                            output[outPos] = output[outPos - copyOffset];
                            outPos++;
                        }
                    }
                }

                return output;
            }
            catch
            {
                return null;
            }
        }

        private static int ReadLiteralLength(byte[] input, byte tag, ref int pos, int end)
        {
            int length = (tag >> 2) + 1;
            if (length <= 60)
                return length;

            int extraBytes = length - 60;
            if (pos + extraBytes > end)
                return -1;

            int decodedLength = 0;
            for (int i = 0; i < extraBytes; i++)
            {
                decodedLength |= (input[pos++] << (8 * i));
            }
            return decodedLength + 1;
        }

        private static bool TryDecodeCopyInstruction(
            byte[] input,
            byte tag,
            int tagType,
            ref int pos,
            int end,
            out int copyLength,
            out int copyOffset)
        {
            copyLength = 0;
            copyOffset = 0;

            if (tagType == TagCopy1ByteOffset)
            {
                if (pos >= end) return false;
                copyLength = ((tag >> 2) & 0x07) + 4;
                copyOffset = ((tag & 0xE0) << 3) | input[pos++];
                return true;
            }

            if (tagType == TagCopy2ByteOffset)
            {
                if (pos + 2 > end) return false;
                copyLength = (tag >> 2) + 1;
                copyOffset = input[pos] | (input[pos + 1] << 8);
                pos += 2;
                return true;
            }

            if (pos + 4 > end) return false;
            copyLength = (tag >> 2) + 1;
            copyOffset = input[pos] | (input[pos + 1] << 8) | (input[pos + 2] << 16) | (input[pos + 3] << 24);
            pos += 4;
            return true;
        }

        public static int ReadVarint32(byte[] data, ref int pos, int end)
        {
            int result = 0;
            int shift = 0;
            while (pos < end && shift <= 28)
            {
                byte b = data[pos++];
                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return result;
                shift += 7;
            }
            return -1;
        }

        public static long ReadVarint64(byte[] data, ref int pos, int end)
        {
            long result = 0;
            int shift = 0;
            while (pos < end && shift <= 63)
            {
                byte b = data[pos++];
                result |= ((long)(b & 0x7F)) << shift;
                if ((b & 0x80) == 0)
                    return result;
                shift += 7;
            }
            return -1;
        }
    }
}
