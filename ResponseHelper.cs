namespace RelayMcp
{
    public class ApiResponse<T>
    {
        public int Code { get; set; }
        public string Message { get; set; } = "";
        public string? ErrorType { get; set; }
        public T? Data { get; set; }
    }

    public static class ResponseHelper
    {
        public static ApiResponse<T> Success<T>(T data, string message = "ok")
            => new() { Code = 200, Message = message, Data = data };

        public static ApiResponse<object> Fail(string message, int code, string errorType, object? data = null)
            => new() { Code = code, Message = message, ErrorType = errorType, Data = data };
    }
}
