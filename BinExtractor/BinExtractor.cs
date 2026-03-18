using System.Collections.Generic;
using System.IO;

namespace BinExtractor
{
    internal class BinExtractor
    {
        static readonly List<string> Unencrypted = new List<string>()
        {
            "SOUND_ID", "VOICE_ID", "SYSTEM"
        };

        static readonly byte[] UniformKey = new byte[]
        {
            0xDF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xDF, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xDF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xDF, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xDF, 0x7F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xDF, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };

        /// <summary>
        /// 每个"指针"用 (source, index) 表示：source=0 表示 UniformKey，source=1 表示 data
        /// </summary>
        private struct Ptr
        {
            public int Source; // 0 = UniformKey, 1 = data
            public int Index;
            public Ptr(int source, int index) { Source = source; Index = index; }

            public byte Deref(byte[] uniformKey, byte[] data)
            {
                return Source == 0 ? uniformKey[Index] : data[Index];
            }

            public byte DerefOffset(byte[] uniformKey, byte[] data, int offset)
            {
                return Source == 0 ? uniformKey[Index + offset] : data[Index + offset];
            }
        }

        public static byte[] Decrypt(byte[] data, int finalSize, int nSegments)
        {
            List<byte> temp = new List<byte>(finalSize);

            // 栈初始化：6个指针分别指向 UniformKey 的 0, 8, 16, 24, 32, 40
            Ptr[] stack = new Ptr[6];
            for (int i = 0; i < 6; i++)
                stack[i] = new Ptr(0, i * 8);

            int readPos = 0; // 当前读取位置（在 data 中）

            int nSteps = finalSize / nSegments;
            int loopRange = nSteps * nSegments;

            while (temp.Count < finalSize)
            {
                byte current = data[readPos];
                byte key = (byte)(current >> 5);

                Ptr oldPointer = default;
                if (key < 6)
                    oldPointer = stack[current >> 5];

                switch (key)
                {
                    case 7:
                        for (int i = 5; i > 0; i--)
                            stack[i] = stack[i - 1];
                        stack[0] = new Ptr(1, readPos);
                        for (int i = 0; i < (current & 0x1F) + 1; i++)
                        {
                            readPos++;
                            temp.Add(data[readPos]);
                        }
                        break;

                    case 6:
                        for (int i = 5; i > 0; i--)
                            stack[i] = stack[i - 1];
                        stack[0] = new Ptr(1, readPos);
                        readPos++;
                        for (int i = 0; i < (current & 0x1F) + 2; i++)
                        {
                            temp.Add(data[readPos]);
                        }
                        break;

                    case 5:
                        stack[5] = stack[4];
                        goto case 4;
                    case 4:
                        stack[4] = stack[3];
                        goto case 3;
                    case 3:
                        stack[3] = stack[2];
                        goto case 2;
                    case 2:
                        stack[2] = stack[1];
                        goto case 1;
                    case 1:
                        stack[1] = stack[0];
                        goto default;
                    default:
                        stack[0] = oldPointer;
                        byte oldKey = (byte)(oldPointer.Deref(UniformKey, data) >> 5);
                        if (oldKey == 7)
                        {
                            int startOffset = (current & 0x1F) / 4 + 1;
                            for (int i = 0; i < (current & 3) + 1; i++)
                            {
                                temp.Add(oldPointer.DerefOffset(UniformKey, data, startOffset + i));
                            }
                        }
                        else if (oldKey == 6)
                        {
                            byte oldByte = oldPointer.DerefOffset(UniformKey, data, 1);
                            for (int i = 0; i < (current & 0x1F) + 2; i++)
                            {
                                temp.Add(oldByte);
                            }
                        }
                        break;
                }
                readPos++;
            }

            // 反交错：转置 temp 数据
            byte[] result = new byte[finalSize];
            int pos = 0;
            for (int i = 0; i < nSteps; i++)
            {
                for (int j = i; j < loopRange; j += nSteps)
                {
                    result[pos++] = temp[j];
                }
            }
            for (int i = loopRange; i < finalSize; i++)
            {
                result[pos++] = temp[i];
            }
            return result;
        }

        private static bool IsBorder(byte[] line)
        {
            if (line == null || line.Length == 0) return false;
            foreach (byte b in line)
                if (b != 0xFF) return false;
            return true;
        }

        private static bool StartsWith(byte[] data, string header, int index = 0)
        {
            for (int i = 0; i < header.Length; i++)
            {
                if (data[index + i] != (byte)header[i]) return false;
            }
            return true;
        }

        private static string GetExtension(byte[] data)
        {
            if (data.Length < 4) return "";
            // TIM2 texture
            if (StartsWith(data, "TIM2")) return ".tm2";
            // BMP image
            if (StartsWith(data, "BM")) return ".bmp";
            // File System Table (FST): starts with [SECTION_NAME]
            if (data[0] == (byte)'[' && data.Length >= 8)
            {
                string head = System.Text.Encoding.ASCII.GetString(
                    data, 0, System.Math.Min(64, data.Length));
                if (head.Contains("]") &&
                    (head.StartsWith("[SYSTEM]") || head.StartsWith("[SCENE") ||
                     head.StartsWith("[NORMAL]") || head.StartsWith("[SOUND")))
                    return ".fst";
            }
            // Squirrel bytecode (FA FA RIQS...)
            if (data.Length >= 6 && StartsWith(data, "RIQS", 2)) return ".nut";
            // Squirrel source code (text with keywords)
            // Some scripts contain Shift-JIS comments, so use Latin-1 for keyword search
            if (data.Length >= 8)
            {
                string text = System.Text.Encoding.GetEncoding(28591).GetString(
                    data, 0, System.Math.Min(1024, data.Length));
                if (text.Contains("function ") || text.Contains("local ") ||
                    text.Contains("if (") || text.Contains("set(") ||
                    text.Contains("reset()") || text.Contains("class ") ||
                    text.Contains("selectInit()") || text.Contains("title(") ||
                    text.Contains("msg(") || text.Contains("back()") ||
                    text.Contains("initVariable()") || text.Contains("Squirrel"))
                    return ".nut";
            }
            // Shift-JIS font mapping table (starts with "　" = 0x81 0x40)
            if (data.Length >= 4 && data[0] == 0x81 && data[1] == 0x40 && data[2] == 0x81)
                return ".fontmap";
            // 3D model data (magic 0x00010000 with float 1.0f = 0x3F800000)
            if (data.Length >= 16)
            {
                int magic = System.BitConverter.ToInt32(data, 0);
                if (magic == 0x00010000)
                {
                    for (int i = 0; i + 4 <= System.Math.Min(64, data.Length); i += 4)
                    {
                        if (System.BitConverter.ToUInt32(data, i) == 0x3F800000)
                            return ".mdl";
                    }
                }
            }
            // Null-padded data table with Shift-JIS strings
            if (data.Length >= 32)
            {
                int checkRange = System.Math.Min(256, data.Length);
                int sjisCount = 0;
                int nullCount = 0;
                for (int i = 0; i < checkRange; i++)
                    if (data[i] == 0) nullCount++;
                for (int i = 0; i < checkRange - 1; i++)
                {
                    if ((data[i] >= 0x81 && data[i] <= 0x9F || data[i] >= 0xE0 && data[i] <= 0xEF) &&
                        data[i + 1] >= 0x40)
                        sjisCount++;
                }
                if (sjisCount >= 3 && nullCount > checkRange / 5)
                    return ".dat";
            }
            // ASCII data table (null-terminated strings)
            if (data.Length >= 8)
            {
                int checkLen = System.Math.Min(64, data.Length);
                int printable = 0, nulls = 0;
                for (int i = 0; i < checkLen; i++)
                {
                    if (data[i] >= 32 && data[i] < 127) printable++;
                    else if (data[i] == 0) nulls++;
                }
                if (printable > 4 && nulls > 2 && (printable + nulls) * 10 >= checkLen * 9)
                    return ".dat";
            }
            return "";
        }

        private static bool IsAsciiText(byte[] data, int checkLen)
        {
            int len = System.Math.Min(checkLen, data.Length);
            for (int i = 0; i < len; i++)
            {
                byte b = data[i];
                if (b >= 128) return false;
                if (b < 32 && b != 9 && b != 10 && b != 13 && b != 0) return false;
            }
            return true;
        }

        public static void Extract(string binPath, string hdPath, string outPath)
        {
            using (BinaryReader binReader = new BinaryReader(File.OpenRead(binPath)))
            using (BinaryReader hdReader = new BinaryReader(File.OpenRead(hdPath)))
            {
                int num = 1;
                byte[] result;
                long binLength = binReader.BaseStream.Length;

                Directory.CreateDirectory(outPath);

                while (hdReader.BaseStream.Position < hdReader.BaseStream.Length)
                {
                    int inputSize = hdReader.ReadInt32();
                    int readSize = (inputSize + 0xF) & ~0xF;
                    while (readSize == 0)
                    {
                        if (hdReader.BaseStream.Position >= hdReader.BaseStream.Length)
                            return;
                        inputSize = hdReader.ReadInt32();
                        readSize = (inputSize + 0xF) & ~0xF;
                    }

                    // 跳过 0xFF border（每次检查16字节，不回退）
                    while (binReader.BaseStream.Position + 0x10 <= binLength)
                    {
                        byte[] line = binReader.ReadBytes(0x10);
                        if (!IsBorder(line))
                        {
                            // 不是 border，回退到这行的开头
                            binReader.BaseStream.Position -= line.Length;
                            break;
                        }
                    }

                    if (binReader.BaseStream.Position + readSize > binLength)
                    {
                        System.Console.WriteLine("  [!] Unexpected end of BIN at file #" + num);
                        break;
                    }

                    if (!Unencrypted.Contains(Path.GetFileNameWithoutExtension(binPath)))
                    {
                        int tempSize = binReader.ReadInt32();
                        int nSegments = binReader.ReadInt32();
                        byte[] block = binReader.ReadBytes(inputSize - 8);
                        result = Decrypt(block, tempSize, nSegments);
                    }
                    else
                    {
                        result = binReader.ReadBytes(inputSize);
                    }
                    binReader.BaseStream.Position += readSize - inputSize;

                    string fileName = string.Format("{0:D4}", num++) + GetExtension(result);
                    string filePath = Path.Combine(outPath, fileName);
                    using (BinaryWriter writer = new BinaryWriter(File.Create(filePath)))
                    {
                        writer.Write(result);
                    }
                }
            }
        }
    }
}
