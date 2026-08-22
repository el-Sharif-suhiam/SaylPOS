using Common.DTOs;
using System;
using System.Collections.Generic;
using System.Text;

namespace Common.Interfaces
{
    public interface ICustomerData
    {
        Task<List<DtoCustomer>> GetCustomersAsync(string search);
        Task<DtoCustomer?> GetCustomerByIdAsync(int id);
        Task<bool> UpdateCustomerAsync(DtoCustomer dto);
    }
}
