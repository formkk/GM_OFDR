using System;
using System.Collections.Generic;

namespace Detection.Core
{
    /// <summary>
    /// 1×8 OCS 路由状态：单输入 COM 选一路输出，0 表示断开。
    /// 取值域来自实测（<c>&lt;SW1-00&gt;</c> ~ <c>&lt;SW1-08&gt;</c>）。
    /// </summary>
    public sealed class Ocs1x8State
    {
        public const int Disconnected = 0;
        public const int MinOutput = 0;
        public const int MaxOutput = 8;

        public int Output { get; }

        public Ocs1x8State(int output)
        {
            if (output < MinOutput || output > MaxOutput)
                throw new ArgumentOutOfRangeException(nameof(output), output, "1×8 输出通道必须在 0..8（0 = 断开）");
            Output = output;
        }

        public bool IsDisconnected
        {
            get { return Output == Disconnected; }
        }

        public override string ToString()
        {
            return "COM-" + (IsDisconnected ? "OFF" : "CH" + Output);
        }

        public override bool Equals(object obj)
        {
            var other = obj as Ocs1x8State;
            return other != null && other.Output == Output;
        }

        public override int GetHashCode()
        {
            return Output;
        }
    }

    /// <summary>
    /// 8×8 OCS 矩阵状态：8 个输入各自指向一个输出，0 表示该路断开。
    /// 索引 0..7 对应 IN1..IN8，值为 OUT1..OUT8 或 0。
    /// </summary>
    public sealed class Ocs8x8State
    {
        public const int Disconnected = 0;
        public const int InputCount = 8;
        public const int MinOutput = 0;
        public const int MaxOutput = 8;

        private readonly int[] _outputs;

        public Ocs8x8State(int[] outputs)
        {
            if (outputs == null) throw new ArgumentNullException(nameof(outputs));
            if (outputs.Length != InputCount)
                throw new ArgumentException("8×8 矩阵必须包含 8 个输出值", nameof(outputs));

            _outputs = new int[InputCount];
            for (int i = 0; i < InputCount; i++)
            {
                int value = outputs[i];
                if (value < MinOutput || value > MaxOutput)
                    throw new ArgumentOutOfRangeException(nameof(outputs), value, "输出通道必须在 0..8（0 = 断开）");
                _outputs[i] = value;
            }
        }

        /// <summary>按输入通道号取输出（input: 1..8）。</summary>
        public int OutputOf(int input)
        {
            if (input < 1 || input > InputCount)
                throw new ArgumentOutOfRangeException(nameof(input), input, "输入通道必须在 1..8");
            return _outputs[input - 1];
        }

        public IReadOnlyList<int> Outputs
        {
            get { return Array.AsReadOnly(_outputs); }
        }

        /// <summary>单位矩阵：IN i → OUT i。</summary>
        public static Ocs8x8State Identity()
        {
            var values = new int[InputCount];
            for (int i = 0; i < InputCount; i++) values[i] = i + 1;
            return new Ocs8x8State(values);
        }

        /// <summary>返回把第 input 路改为 output 后的新状态（1..8）。</summary>
        public Ocs8x8State With(int input, int output)
        {
            if (input < 1 || input > InputCount)
                throw new ArgumentOutOfRangeException(nameof(input), input, "输入通道必须在 1..8");
            if (output < MinOutput || output > MaxOutput)
                throw new ArgumentOutOfRangeException(nameof(output), output, "输出通道必须在 0..8（0 = 断开）");

            var copy = new int[InputCount];
            Array.Copy(_outputs, copy, InputCount);
            copy[input - 1] = output;
            return new Ocs8x8State(copy);
        }

        /// <summary>找出占用指定非 0 输出的其他输入通道；无冲突返回 0。</summary>
        public int FindOccupant(int output, int excludingInput)
        {
            if (output == Disconnected) return 0;
            for (int i = 0; i < InputCount; i++)
            {
                int input = i + 1;
                if (input == excludingInput) continue;
                if (_outputs[i] == output) return input;
            }
            return 0;
        }

        public override string ToString()
        {
            var parts = new string[InputCount];
            for (int i = 0; i < InputCount; i++) parts[i] = "IN" + (i + 1) + "->" + (_outputs[i] == 0 ? "OFF" : "OUT" + _outputs[i]);
            return string.Join(", ", parts);
        }

        public override bool Equals(object obj)
        {
            var other = obj as Ocs8x8State;
            if (other == null) return false;
            for (int i = 0; i < InputCount; i++)
            {
                if (_outputs[i] != other._outputs[i]) return false;
            }
            return true;
        }

        public override int GetHashCode()
        {
            int hash = 17;
            for (int i = 0; i < InputCount; i++) hash = hash * 31 + _outputs[i];
            return hash;
        }
    }
}
