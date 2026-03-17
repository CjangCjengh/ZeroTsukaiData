using System;
using System.Collections.Generic;
using System.IO;

namespace BinPacker
{
    internal class BinPacker
    {
        public static void Pack(string dirPath, string binPath, string hdPath)
        {
            BinaryWriter binWriter = new BinaryWriter(File.Create(binPath));
            BinaryWriter hdWriter = new BinaryWriter(File.Create(hdPath));
            foreach (string file in Directory.GetFiles(dirPath))
            {
                BinaryReader reader = new BinaryReader(File.OpenRead(file));
                byte[] data = reader.ReadBytes((int)reader.BaseStream.Length);
                data = Encrypt(data, out int finalSize);
                binWriter.Write(data);
                hdWriter.Write(BitConverter.GetBytes(finalSize));
            }
            binWriter.Close();
            hdWriter.Close();
        }

        // 栈条目：记录已发射的 case7/case6 指令信息，用于回引匹配
        struct StackEntry
        {
            public int Type;        // 7 = 字面量, 6 = RLE
            public byte[] Literals; // case7: 字面量数据; case6: null
            public byte RleByte;    // case6: 重复的字节
            public int RleCount;    // case6: 重复次数
        }

        static byte[] Encrypt(byte[] data, out int finalSize, int nSegments = 1)
        {
            // 第一步：交织重排（与Decrypt末尾的反交织对应）
            List<byte> temp1 = new List<byte>();
            int nSteps = data.Length / nSegments;
            int loopRange = nSteps * nSegments;
            for (int i = 0; i < nSegments; i++)
            {
                for (int j = i; j < loopRange; j += nSegments)
                {
                    temp1.Add(data[j]);
                }
            }
            for (int i = loopRange; i < data.Length; i++)
            {
                temp1.Add(data[i]);
            }

            byte[] input = temp1.ToArray();
            List<byte> output = new List<byte>();

            // 维护一个与Decrypt一致的6元素栈（存储StackEntry而非指针）
            StackEntry[] stack = new StackEntry[6];
            // 初始栈内容对应UniformKey：全部是case6类型(0xDF >> 5 = 6)
            // UniformKey中每个条目: [0xDF, val, 0,0,0,0,0,0]
            // case6: rleByte = *(ptr+1), rleCount由引用时决定
            byte[] uniformVals = { 0x00, 0x01, 0xFF, 0x80, 0x7F, 0xFE };
            for (int i = 0; i < 6; i++)
            {
                stack[i] = new StackEntry
                {
                    Type = 6,
                    RleByte = uniformVals[i],
                    RleCount = 0,
                    Literals = null
                };
            }

            int pos = 0;
            while (pos < input.Length)
            {
                int remaining = input.Length - pos;

                // ===== 策略1: 尝试用栈回引case6（RLE回引）获得最优匹配 =====
                // 回引case6条目：1字节指令可输出 2~33 个相同字节
                int bestRleRefSaving = 0;
                int bestRleRefStackIdx = -1;
                int bestRleRefLen = 0;

                for (int si = 0; si < 6; si++)
                {
                    if (stack[si].Type != 6) continue;
                    byte val = stack[si].RleByte;
                    // 检查从pos开始有多少个连续的val
                    int runLen = 0;
                    while (runLen < remaining && runLen < 33 && input[pos + runLen] == val)
                        runLen++;
                    if (runLen >= 2)
                    {
                        // 回引case6: 1字节编码，输出runLen个字节
                        // 节省 = runLen - 1（相比1字节开销）
                        int saving = runLen - 1;
                        if (saving > bestRleRefSaving)
                        {
                            bestRleRefSaving = saving;
                            bestRleRefStackIdx = si;
                            bestRleRefLen = runLen;
                        }
                    }
                }

                // ===== 策略2: 尝试直接发射case6（新RLE）=====
                // case6: 2字节编码（指令+数据字节），输出 2~33 个相同字节
                int directRleLen = 1;
                byte rleByte = input[pos];
                while (directRleLen < remaining && directRleLen < 33 && input[pos + directRleLen] == rleByte)
                    directRleLen++;
                int directRleSaving = (directRleLen >= 2) ? directRleLen - 2 : -1;

                // ===== 策略3: 尝试用栈回引case7（字面量回引）=====
                // 回引case7条目：1字节编码，输出1~4字节
                // 编码: stackIdx<<5 | (offset<<2) | (length-1)
                // offset范围0~7, length范围1~4
                int bestLitRefSaving = 0;
                int bestLitRefStackIdx = -1;
                int bestLitRefOffset = 0;
                int bestLitRefLen = 0;

                for (int si = 0; si < 6; si++)
                {
                    if (stack[si].Type != 7) continue;
                    byte[] lits = stack[si].Literals;
                    // 尝试各个offset(0~7)和length(1~4)
                    for (int off = 0; off < 8 && off < lits.Length; off++)
                    {
                        int maxLen = Math.Min(4, Math.Min(remaining, lits.Length - off));
                        int matchLen = 0;
                        for (int k = 0; k < maxLen; k++)
                        {
                            if (lits[off + k] == input[pos + k])
                                matchLen = k + 1;
                            else
                                break;
                        }
                        if (matchLen >= 1)
                        {
                            // 1字节编码输出matchLen个字节，净节省 = matchLen - 1
                            int saving = matchLen - 1;
                            if (saving > bestLitRefSaving ||
                                (saving == bestLitRefSaving && matchLen > bestLitRefLen))
                            {
                                bestLitRefSaving = saving;
                                bestLitRefStackIdx = si;
                                bestLitRefOffset = off;
                                bestLitRefLen = matchLen;
                            }
                        }
                    }
                }

                // ===== 选择最优策略 =====
                // 比较三种策略的净节省量
                int bestSaving = 0;
                int bestChoice = 0; // 0=字面量, 1=RLE回引, 2=直接RLE, 3=字面量回引

                if (bestRleRefSaving > bestSaving)
                {
                    bestSaving = bestRleRefSaving;
                    bestChoice = 1;
                }
                if (directRleSaving > bestSaving)
                {
                    bestSaving = directRleSaving;
                    bestChoice = 2;
                }
                if (bestLitRefSaving > bestSaving)
                {
                    bestSaving = bestLitRefSaving;
                    bestChoice = 3;
                }

                if (bestChoice == 1)
                {
                    // 发射栈回引case6指令
                    int si = bestRleRefStackIdx;
                    int len = bestRleRefLen;
                    byte cmd = (byte)((si << 5) | (len - 2));
                    output.Add(cmd);
                    // 更新栈：cascade下推，将引用的条目提升到stack[0]
                    PushStack(stack, si);
                    pos += len;
                }
                else if (bestChoice == 2)
                {
                    // 发射直接case6指令
                    byte cmd = (byte)(0xC0 | (directRleLen - 2));
                    output.Add(cmd);
                    output.Add(rleByte);
                    // 栈下推，新条目入栈
                    for (int i = 5; i > 0; i--)
                        stack[i] = stack[i - 1];
                    stack[0] = new StackEntry
                    {
                        Type = 6,
                        RleByte = rleByte,
                        RleCount = directRleLen,
                        Literals = null
                    };
                    pos += directRleLen;
                }
                else if (bestChoice == 3)
                {
                    // 发射栈回引case7指令
                    int si = bestLitRefStackIdx;
                    int off = bestLitRefOffset;
                    int len = bestLitRefLen;
                    byte cmd = (byte)((si << 5) | (off << 2) | (len - 1));
                    output.Add(cmd);
                    // 更新栈：cascade下推
                    PushStack(stack, si);
                    pos += len;
                }
                else
                {
                    // 发射case7字面量：尽可能收集更多字节（最多32字节）
                    // 但要在遇到可压缩模式时提前截断
                    int litLen = 0;
                    int maxLit = Math.Min(32, remaining);
                    while (litLen < maxLit)
                    {
                        // 向前探测：下一个位置是否有更好的压缩机会？
                        if (litLen > 0)
                        {
                            int nextPos = pos + litLen;
                            int nextRemaining = input.Length - nextPos;
                            // 检查是否有≥4字节的RLE机会
                            if (nextRemaining >= 4 && input[nextPos] == input[nextPos + 1]
                                && input[nextPos] == input[nextPos + 2]
                                && input[nextPos] == input[nextPos + 3])
                                break;
                            // 检查是否有栈回引RLE的机会（≥3字节连续）
                            if (nextRemaining >= 3)
                            {
                                bool hasRleRef = false;
                                for (int si = 0; si < 6; si++)
                                {
                                    if (stack[si].Type == 6 && input[nextPos] == stack[si].RleByte)
                                    {
                                        int run = 0;
                                        while (run < nextRemaining && run < 33 && input[nextPos + run] == stack[si].RleByte)
                                            run++;
                                        if (run >= 3) { hasRleRef = true; break; }
                                    }
                                }
                                if (hasRleRef) break;
                            }
                        }
                        litLen++;
                    }
                    if (litLen == 0) litLen = 1;

                    byte cmd = (byte)(0xE0 | (litLen - 1));
                    output.Add(cmd);
                    byte[] lits = new byte[litLen];
                    for (int i = 0; i < litLen; i++)
                    {
                        output.Add(input[pos + i]);
                        lits[i] = input[pos + i];
                    }
                    // 栈下推，新字面量条目入栈
                    for (int i = 5; i > 0; i--)
                        stack[i] = stack[i - 1];
                    stack[0] = new StackEntry
                    {
                        Type = 7,
                        Literals = lits,
                        RleByte = 0,
                        RleCount = 0
                    };
                    pos += litLen;
                }
            }

            finalSize = output.Count + 8;
            byte[] result = new byte[(finalSize + 0xF) & ~0xF];
            BitConverter.GetBytes(data.Length).CopyTo(result, 0);
            BitConverter.GetBytes(nSegments).CopyTo(result, 4);
            output.CopyTo(result, 8);
            for (int i = finalSize; i < result.Length; i++)
                result[i] = 0xFF;
            return result;
        }

        /// <summary>
        /// 模拟Decrypt中case 0~5的栈cascade下推操作。
        /// 对于case N: stack[N]=stack[N-1], ..., stack[1]=stack[0], stack[0]=被引用的条目
        /// </summary>
        static void PushStack(StackEntry[] stack, int refIdx)
        {
            StackEntry referenced = stack[refIdx];
            for (int i = refIdx; i > 0; i--)
            {
                stack[i] = stack[i - 1];
            }
            stack[0] = referenced;
        }
    }
}
