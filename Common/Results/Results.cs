using System;
using System.Collections.Generic;
using System.Text;
using Common.DTOs;

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

        /// <summary>نتيجة رفض منظمة: تجاوز حد ائتمان العميل — يتطلب موافقة المدير.
        /// machine-distinguishable: المستدعي يفحص <c>result is CreditApprovalRequiredResult</c>
        /// (أو نسخة Result&lt;T&gt;) ليفرّقها عن فشل تقني/تحقق عام وتشغّل سير الاعتماد.
        /// لا تُكمل العملية أبدًا — البيع المرفوض لم يُقَد ولا صُدِر أثره.</summary>
        public class CreditApprovalRequiredResult : Result
        {
            public CreditApprovalRequiredInfo? ApprovalInfo { get; set; }

            public static CreditApprovalRequiredResult Fail(CreditApprovalRequiredInfo info, string msg)
                => new() { IsSuccess = false, Message = msg, ApprovalInfo = info };
        }

        public class CreditApprovalRequiredResult<T> : Result<T>
        {
            public CreditApprovalRequiredInfo? ApprovalInfo { get; set; }

            public static CreditApprovalRequiredResult<T> Fail(CreditApprovalRequiredInfo info, string msg)
                => new() { IsSuccess = false, Message = msg, ApprovalInfo = info };
        }
    
}
