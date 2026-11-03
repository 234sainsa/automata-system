namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// What happened during a turn.
    /// 番の途中の出来事の種類。
    /// </summary>
    public enum ChatGptTurnEventKind
    {
        /// <summary>A piece of the reply text. 答えの文の途中（Text に足す分）。</summary>
        TextDelta = 0,

        /// <summary>The model called a tool. AI が道具を呼んだ（ToolName・ToolArguments・ToolCallId）。</summary>
        ToolCall,

        /// <summary>The tool answered. 道具が答えた（Text が AI へ渡す答え・IsError）。</summary>
        ToolResult,

        /// <summary>The turn ended with the final text. 番が終わった（Text が最後の文）。</summary>
        TurnEnd,
    }

    /// <summary>
    /// One event of a turn, given to the caller while the turn runs.
    /// 番の途中の出来事 1 つ。番を回している間に、呼ぶ側へ渡す。
    /// </summary>
    public sealed class ChatGptTurnEvent
    {
        internal ChatGptTurnEvent(ChatGptTurnEventKind kind, string text, string toolName = null, string toolArguments = null, string toolCallId = null, bool isError = false)
        {
            this.Kind = kind;
            this.Text = text;
            this.ToolName = toolName;
            this.ToolArguments = toolArguments;
            this.ToolCallId = toolCallId;
            this.IsError = isError;
        }

        /// <summary>The kind. 種類。</summary>
        public ChatGptTurnEventKind Kind { get; }

        /// <summary>The text (delta, tool output, or final text). 字（文の途中・道具の答え・最後の文）。</summary>
        public string Text { get; }

        /// <summary>The tool name, or null. 道具の名前（道具の出来事の時だけ）。</summary>
        public string ToolName { get; }

        /// <summary>The raw tool arguments (JSON text), or null. AI が渡した引数（JSON の字）。</summary>
        public string ToolArguments { get; }

        /// <summary>The call id, or null. 呼び出しの印（call_id）。</summary>
        public string ToolCallId { get; }

        /// <summary>True when the tool reply is an error. 道具の答えが断り・失敗か。</summary>
        public bool IsError { get; }
    }

    /// <summary>
    /// The result of one finished turn.
    /// 済んだ番 1 つの結果。
    /// </summary>
    public sealed class ChatGptTurnResult
    {
        internal ChatGptTurnResult(string text, int toolCalls, int requests)
        {
            this.Text = text;
            this.ToolCalls = toolCalls;
            this.Requests = requests;
        }

        /// <summary>The final reply text. 最後の文。</summary>
        public string Text { get; }

        /// <summary>How many tools were called. 道具を呼んだ回数。</summary>
        public int ToolCalls { get; }

        /// <summary>How many requests were sent. 頼んだ回数（送り直しも数える）。</summary>
        public int Requests { get; }
    }
}
