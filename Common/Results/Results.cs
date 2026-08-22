using System;
using System.Collections.Generic;
using System.Text;

namespace Common.Results
{
        public class Result
        {
            public bool IsSuccess { get; set; }
            public string Message { get; set; } = "";

            public static Result Ok(string msg = "") => new() { IsSuccess = true, Message = msg };
            public static Result Fail(string msg) => new() { IsSuccess = false, Message = msg };
        }

        public class Result<T> : Result
        {
            public T? Data { get; set; }

            public static Result<T> Ok(T data, string msg = "") => new() { IsSuccess = true, Message = msg, Data = data };
            public new static Result<T> Fail(string msg) => new() { IsSuccess = false, Message = msg };
        }
    
}
