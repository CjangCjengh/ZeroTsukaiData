using System.Collections.Generic;
using System.IO;

namespace BinExtractor
{
    internal class BinExtractor
    {
        static readonly List<string> FilesEncrypted = new List<string>()
        {
            "NORMAL", "SCENE_ID", "SCENEDAT"
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

        public static unsafe byte[] Decrypt(byte[] data, int tempSize, int nSegments)
        {
            List<byte> result = new List<byte>();
            List<byte> temp = new List<byte>();
            byte*[] stack = new byte*[6];
            fixed (byte* p = UniformKey)
            {
                for (int i = 0; i < 6; i++)
                    stack[i] = p + i * 8;
            }
            byte* readPointer;
            fixed (byte* p = data)
            {
                readPointer = p;
            }
            int nSteps = tempSize / nSegments;
            int loopRange = nSteps * nSegments;
            while (temp.Count < tempSize)
            {
                byte current = *readPointer;
                byte key = (byte)(current >> 5);
                byte* oldPointer = null;
                if (key < 6)
                    oldPointer = stack[current >> 5];
                switch (key)
                {
                    case 7:
                        for (int i = 5; i > 0; i--)
                        {
                            stack[i] = stack[i - 1];
                        }
                        stack[0] = readPointer;
                        for (int i = 0; i < (current & 0x1F) + 1; i++)
                        {
                            temp.Add(*(++readPointer));
                        }
                        break;
                    case 6:
                        for (int i = 5; i > 0; i--)
                        {
                            stack[i] = stack[i - 1];
                        }
                        stack[0] = readPointer++;
                        for (int i = 0; i < (current & 0x1F) + 2; i++)
                        {
                            temp.Add(*readPointer);
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
                        key = (byte)(*oldPointer >> 5);
                        if (key == 7)
                        {
                            byte* start = oldPointer + (current & 0x1F) / 4 + 1;
                            for (int i = 0; i < (current & 3) + 1; i++)
                            {
                                temp.Add(*(start++));
                            }
                        }
                        else if (key == 6)
                        {
                            byte oldByte = *(oldPointer + 1);
                            for (int i = 0; i < (current & 0x1F) + 2; i++)
                            {
                                temp.Add(oldByte);
                            }
                        }
                        break;
                }
                readPointer++;
            }
            if (nSteps > 0)
            {
                for (int i = 0; i < nSteps; i++)
                {
                    for (int j = i; j < loopRange; j += nSteps)
                    {
                        result.Add(temp[j]);
                    }
                }
            }
            for (int i = loopRange; i < tempSize; i++)
            {
                result.Add(temp[i]);
            }
            return result.ToArray();
        }

        private static bool IsBorder(byte[] line)
        {
            foreach (byte b in line)
                if (b != '\xFF') return false;
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
            if (StartsWith(data, "BM")) return ".bmp";
            else if (StartsWith(data, "TIM2")) return ".tm2";
            else if (StartsWith(data, "RIQS", 2)) return ".nut";
            else return "";
        }

        public static void Extract(string binPath, string hdPath, string outPath)
        {
            BinaryReader binReader = new BinaryReader(File.OpenRead(binPath));
            BinaryReader hdReader = new BinaryReader(File.OpenRead(hdPath));
            int num = 1;
            byte[] line;
            byte[] result;

            Directory.CreateDirectory(outPath);

            while (hdReader.BaseStream.Position < hdReader.BaseStream.Length)
            {
                int inputSize = hdReader.ReadInt32();
                int readSize = ((inputSize + 0xF) & ~0xF);
                while (readSize == 0)
                {
                    inputSize = hdReader.ReadInt32();
                    readSize = ((inputSize + 0xF) & ~0xF);
                }

                do
                {
                    line = binReader.ReadBytes(0x10);
                } while (IsBorder(line));
                binReader.BaseStream.Position -= 0x10;

                if (FilesEncrypted.Contains(Path.GetFileNameWithoutExtension(binPath)))
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
                BinaryWriter writer = new BinaryWriter(File.Open(Path.Combine(outPath, fileName), FileMode.Create));
                writer.Write(result);
                writer.Close();
            }
            binReader.Close();
            hdReader.Close();
        }
    }
}
