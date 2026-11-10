namespace KaraokeFilmMaker.Infrastructure.Ai;

public enum ReadingFailureCode
{
    EventTooLarge, TransportLimit, ContentLimit, SentenceLimit,
    IncompleteStream, Truncated, Filtered, UnexpectedFinish, ProviderError, EmptyTranscript, EmptyAnswer
}

public sealed class ReadingServiceFailure(ReadingFailureCode code) : InvalidOperationException(code switch
{
    ReadingFailureCode.EventTooLarge => "单个流式数据包超过长度限制。",
    ReadingFailureCode.TransportLimit => "流式传输总量超过安全上限。",
    ReadingFailureCode.ContentLimit => "正式答案超过长度限制。",
    ReadingFailureCode.SentenceLimit => "单句校正结果超过长度限制。",
    ReadingFailureCode.IncompleteStream => "服务连接提前结束，未收到完整结束标记。",
    ReadingFailureCode.Truncated => "服务端输出达到长度上限，答案被截断。",
    ReadingFailureCode.Filtered => "服务端内容过滤导致输出未完成。",
    ReadingFailureCode.UnexpectedFinish => "服务端返回了不支持的结束原因。",
    ReadingFailureCode.ProviderError => "服务在流式响应中返回错误。",
    ReadingFailureCode.EmptyAnswer => "服务已结束但没有正式答案（可能仅返回思考或空白），请检查模型配置。",
    ReadingFailureCode.EmptyTranscript => "ASR 未返回可用的识别文本。",
    _ => "识别或校正未完成。"
})
{
    public ReadingFailureCode Code { get; } = code;
}
