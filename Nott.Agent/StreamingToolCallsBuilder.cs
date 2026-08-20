using System.Buffers;
using System.Diagnostics;
using OpenAI.Chat;

namespace Nott.Agent;


public class StreamingToolCallsBuilder
{
    private class SequenceBuilder<T>
    {
        private class Segment<TItem> : ReadOnlySequenceSegment<TItem>
        {
            public Segment(ReadOnlyMemory<TItem> memory, long runningIndex)
            {
                Memory = memory;
                RunningIndex = runningIndex;
            }

            public Segment<TItem> Append(ReadOnlyMemory<TItem> memory)
            {
                long newRunningIndex;
                checked
                {
                    newRunningIndex = RunningIndex + memory.Length;
                }

                var seg = new Segment<TItem>(memory, newRunningIndex);
                Next = seg;
                return seg;
            }
        }

        private Segment<T>? _begin;
        private Segment<T>? _end;

        public void AddSegment(ReadOnlyMemory<T> memory)
        {
            if (_begin == null)
            {
                Debug.Assert(_end == null);

                _begin = new Segment<T>(memory, 0);
                _end = _begin;
            }
            else
            {
                _end = _end!.Append(memory);
            }
        }

        public ReadOnlySequence<T> BuildSequence()
        {
            if (_begin == null)
            {
                Debug.Assert(_end == null);
                return ReadOnlySequence<T>.Empty;
            }

            if (_begin == _end)
            {
                Debug.Assert(_begin.Next == null);
                return new ReadOnlySequence<T>(_begin.Memory);
            }

            return new ReadOnlySequence<T>(_begin, 0, _end!, _end!.Memory.Length);
        }
    }

    private readonly Dictionary<int, string> _idxToCallId = new();
    private readonly Dictionary<int, string> _idxToFunctionName = new();
    private readonly Dictionary<int, SequenceBuilder<byte>> _idxToFuncArgumentsSequence = new();

    public void AddStreamingUpdate(StreamingChatToolCallUpdate update)
    {
        if (update.ToolCallId != null)
        {
            _idxToCallId[update.Index] = update.ToolCallId;
        }

        if (update.FunctionName != null)
        {
            _idxToFunctionName[update.Index] = update.FunctionName;
        }

        if (update.FunctionArgumentsUpdate != null && !update.FunctionArgumentsUpdate.ToMemory().IsEmpty)
        {
            if (!_idxToFuncArgumentsSequence.TryGetValue(update.Index, out var funcArgs))
            {
                funcArgs = _idxToFuncArgumentsSequence[update.Index] = new SequenceBuilder<byte>();
            }

            funcArgs.AddSegment(update.FunctionArgumentsUpdate);
        }
    }

    public IReadOnlyList<ChatToolCall> Build()
    {
        var toolCalls = new List<ChatToolCall>();

        foreach (var (idx, id) in _idxToCallId)
        {
            var arguments = _idxToFuncArgumentsSequence.TryGetValue(idx, out var argumentSequence)
                ? BinaryData.FromBytes(argumentSequence.BuildSequence().ToArray())
                : BinaryData.FromString("{}");
            toolCalls.Add(ChatToolCall.CreateFunctionToolCall(id, _idxToFunctionName[idx], arguments));
        }

        return toolCalls;
    }
}
