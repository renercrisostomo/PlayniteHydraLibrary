using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HydraPlaynite.Services
{
    public static class LevelDbFileReader
    {
        private const int LogBlockSizeBytes = 32768;
        private const int LogHeaderSizeBytes = 7;
        private const int SstableFooterSizeBytes = 48;
        private const int MaxJsonNestingDepth = 8;
        private const int MaxJsonLengthBytes = 65536;

        public static List<string> ReadAllJsonFromFile(string filePath)
        {
            List<string> allJsons = new List<string>();
            byte[] rawBytes = ReadFileUnlocked(filePath);
            if (rawBytes == null || rawBytes.Length == 0)
                return allJsons;

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".log")
            {
                byte[] cleanLog = ReadLogPayload(rawBytes);
                if (cleanLog != null && cleanLog.Length > 0)
                {
                    allJsons.AddRange(ExtractBalancedJsonObjects(cleanLog));
                }
                if (allJsons.Count == 0)
                {
                    allJsons.AddRange(ExtractBalancedJsonObjects(rawBytes));
                }
            }
            else
            {
                foreach (byte[] block in ExtractLdbBlocks(rawBytes))
                {
                    allJsons.AddRange(ExtractBalancedJsonObjects(block));
                }
            }

            return allJsons;
        }

        public static byte[] ReadFileUnlocked(string filePath)
        {
            try
            {
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] rawBytes = new byte[fs.Length];
                    int totalRead = 0;
                    while (totalRead < rawBytes.Length)
                    {
                        int bytesRead = fs.Read(rawBytes, totalRead, rawBytes.Length - totalRead);
                        if (bytesRead <= 0) break;
                        totalRead += bytesRead;
                    }
                    return rawBytes;
                }
            }
            catch
            {
                return TryReadViaTemporaryCopy(filePath);
            }
        }

        public static byte[] ReadLogPayload(byte[] rawLog)
        {
            using (MemoryStream ms = new MemoryStream(rawLog.Length))
            {
                int offset = 0;
                while (offset + LogHeaderSizeBytes <= rawLog.Length)
                {
                    int blockRemaining = LogBlockSizeBytes - (offset % LogBlockSizeBytes);
                    if (blockRemaining < LogHeaderSizeBytes)
                    {
                        offset += blockRemaining;
                        continue;
                    }

                    int recordLength = rawLog[offset + 4] | (rawLog[offset + 5] << 8);
                    byte recordType = rawLog[offset + 6];
                    offset += LogHeaderSizeBytes;

                    if (recordLength < 0 || offset + recordLength > rawLog.Length)
                        break;

                    if (recordType >= 1 && recordType <= 4 && recordLength > 0)
                    {
                        ms.Write(rawLog, offset, recordLength);
                    }
                    offset += recordLength;
                }
                return ms.ToArray();
            }
        }

        public static List<byte[]> ExtractLdbBlocks(byte[] rawLdb)
        {
            List<byte[]> blocks = new List<byte[]>();

            try
            {
                if (rawLdb.Length >= SstableFooterSizeBytes && HasValidSstableMagicNumber(rawLdb))
                {
                    int fPos = rawLdb.Length - SstableFooterSizeBytes;
                    int fEnd = rawLdb.Length - 8;
                    SnappyDecompressor.ReadVarint64(rawLdb, ref fPos, fEnd); // metaOffset
                    SnappyDecompressor.ReadVarint64(rawLdb, ref fPos, fEnd); // metaSize
                    long indexOffset = SnappyDecompressor.ReadVarint64(rawLdb, ref fPos, fEnd);
                    long indexSize = SnappyDecompressor.ReadVarint64(rawLdb, ref fPos, fEnd);

                    byte[] indexBlock = ReadSstBlock(rawLdb, (int)indexOffset, (int)indexSize);
                    if (indexBlock != null && indexBlock.Length >= 4)
                    {
                        int numRestarts = indexBlock[indexBlock.Length - 4] |
                                          (indexBlock[indexBlock.Length - 3] << 8) |
                                          (indexBlock[indexBlock.Length - 2] << 16) |
                                          (indexBlock[indexBlock.Length - 1] << 24);

                        int entriesEnd = indexBlock.Length - 4 - (numRestarts * 4);
                        int iPos = 0;
                        while (iPos < entriesEnd)
                        {
                            int shared = SnappyDecompressor.ReadVarint32(indexBlock, ref iPos, entriesEnd);
                            int unshared = SnappyDecompressor.ReadVarint32(indexBlock, ref iPos, entriesEnd);
                            int valLen = SnappyDecompressor.ReadVarint32(indexBlock, ref iPos, entriesEnd);
                            if (shared < 0 || unshared < 0 || valLen < 0 || iPos + unshared + valLen > entriesEnd)
                                break;

                            iPos += unshared;
                            int vPos = iPos;
                            int vEnd = iPos + valLen;
                            long dataOffset = SnappyDecompressor.ReadVarint64(indexBlock, ref vPos, vEnd);
                            long dataSize = SnappyDecompressor.ReadVarint64(indexBlock, ref vPos, vEnd);
                            iPos += valLen;

                            if (dataOffset >= 0 && dataSize > 0)
                            {
                                byte[] dataBlock = ReadSstBlock(rawLdb, (int)dataOffset, (int)dataSize);
                                if (dataBlock != null)
                                {
                                    blocks.Add(dataBlock);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            blocks.Add(rawLdb);
            return blocks;
        }

        public static List<string> ExtractBalancedJsonObjects(byte[] buffer)
        {
            List<string> results = new List<string>();
            if (buffer == null || buffer.Length == 0)
                return results;

            string text = Encoding.UTF8.GetString(buffer);
            int n = text.Length;
            int i = 0;

            while (i < n)
            {
                int start = text.IndexOf('{', i);
                if (start == -1) break;

                int depth = 0;
                bool inString = false;
                bool escaped = false;
                bool valid = true;
                int end = -1;

                for (int j = start; j < n; j++)
                {
                    char c = text[j];

                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (c == '\\')
                    {
                        if (inString) escaped = true;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = !inString;
                        continue;
                    }

                    if (!inString)
                    {
                        if (c == '{')
                        {
                            depth++;
                            if (depth > MaxJsonNestingDepth)
                            {
                                valid = false;
                                break;
                            }
                        }
                        else if (c == '}')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                end = j;
                                break;
                            }
                        }
                        else if (IsInvalidControlChar(c))
                        {
                            valid = false;
                            break;
                        }
                    }
                    else if (IsInvalidControlChar(c))
                    {
                        valid = false;
                        break;
                    }

                    if (j - start > MaxJsonLengthBytes)
                    {
                        valid = false;
                        break;
                    }
                }

                if (valid && end != -1)
                {
                    string candidate = text.Substring(start, end - start + 1);
                    if (IsRelevantHydraJson(candidate))
                    {
                        results.Add(candidate);
                    }
                    i = end + 1;
                }
                else
                {
                    i = start + 1;
                }
            }

            return results;
        }

        private static bool HasValidSstableMagicNumber(byte[] rawLdb)
        {
            int m = rawLdb.Length - 8;
            return rawLdb[m] == 0x57 && rawLdb[m + 1] == 0xFB &&
                   rawLdb[m + 2] == 0x80 && rawLdb[m + 3] == 0x8B &&
                   rawLdb[m + 4] == 0x24 && rawLdb[m + 5] == 0x75 &&
                   rawLdb[m + 6] == 0x47 && rawLdb[m + 7] == 0xDB;
        }

        private static bool IsInvalidControlChar(char c)
        {
            return c < 0x09 || (c > 0x0D && c < 0x20);
        }

        private static bool IsRelevantHydraJson(string candidate)
        {
            return candidate.Contains("\"title\"") ||
                   candidate.Contains("\"folderName\"") ||
                   candidate.Contains("\"downloader\"") ||
                   candidate.Contains("\"recompilationSlug\"") ||
                   candidate.Contains("\"executablePath\"") ||
                   candidate.Contains("\"customIconUrl\"") ||
                   candidate.Contains("\"customOriginalIconPath\"") ||
                   candidate.Contains("\"customCoverImageUrl\"") ||
                   candidate.Contains("\"customHeroImageUrl\"");
        }

        private static byte[] ReadSstBlock(byte[] rawLdb, int blockOffset, int blockSize)
        {
            if (blockOffset < 0 || blockSize <= 0 || blockOffset + blockSize + 1 > rawLdb.Length)
                return null;

            byte compressionType = rawLdb[blockOffset + blockSize];
            if (compressionType == 0)
            {
                byte[] raw = new byte[blockSize];
                Buffer.BlockCopy(rawLdb, blockOffset, raw, 0, blockSize);
                return raw;
            }
            if (compressionType == 1)
            {
                return SnappyDecompressor.Decompress(rawLdb, blockOffset, blockSize);
            }
            return null;
        }

        private static byte[] TryReadViaTemporaryCopy(string filePath)
        {
            try
            {
                string tempCopy = Path.Combine(Path.GetTempPath(), "hydra_ldb_" + Guid.NewGuid().ToString("N") + ".tmp");
                File.Copy(filePath, tempCopy, true);
                byte[] bytes = File.ReadAllBytes(tempCopy);
                try { File.Delete(tempCopy); } catch { }
                return bytes;
            }
            catch
            {
                return null;
            }
        }
    }
}
