using Common.DTOs;
using System;
using System.Collections.Generic;
using System.Text;

namespace Common.Utils
{
    public class clsCompare
    {
        public static bool IsProductChanged(DtoProduct oldProduct,DtoProduct newProduct)
        {
            if (!string.Equals(oldProduct.Name?.Trim(),
                               newProduct.Name?.Trim(),
                               StringComparison.OrdinalIgnoreCase))
                return true;

            if (oldProduct.BasePrice != newProduct.BasePrice)
                return true;

            if (!string.Equals(oldProduct.Barcode ?? "",
                               newProduct.Barcode ?? "",
                               StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        
    }
}
