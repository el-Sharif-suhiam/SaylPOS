using System;
using System.Collections.Generic;

namespace Common.DTOs
{
    /// <summary>
    /// صفحة بيانات مؤرشفة من المصدر (Skip/Take على مستوى SQL) مع العدد
    /// الكلي — محور الترقيم في كل قوائم النظام.
    /// </summary>
    public class PagedResult<T>
    {
        public List<T> Items { get; set; } = new();
        public int TotalCount { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 20;

        public int TotalPages => PageSize > 0
            ? (int)Math.Ceiling(TotalCount / (double)PageSize)
            : 0;

        public static PagedResult<T> Empty(int page = 1, int pageSize = 20) => new()
        {
            Items = new List<T>(),
            TotalCount = 0,
            Page = page,
            PageSize = pageSize
        };
    }
}