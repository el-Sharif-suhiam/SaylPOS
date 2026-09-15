using Common.DTOs;
using Common.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Sayl.Core.Data;
using Sayl.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Sayl.Core.Services
{
    public class clsSupplierService
    {
        private readonly SaylDbContext _dbContext;

        public clsSupplierService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<List<Supplier>> GetSupplierListAsync(string search = "", bool includeInactive = false)
        {
            var query = _dbContext.Suppliers.Include(c => c.Party)
                .Where(c => string.IsNullOrWhiteSpace(search) || c.Party.Name.Contains(search) || c.Party.Email.Contains(search));

            // الافتراضي: الموردون النشطون فقط — العرض الكامل عند الطلب من الواجهة
            if (!includeInactive)
                query = query.Where(c => c.Party.IsActive);

            query = query.OrderBy(c => c.Party.Name);

            List<Supplier> suppliers = await query.ToListAsync();

            return suppliers;
        }

        /// <summary>صفحة موردين (ترقيم SQL): نفس فلاتر القائمة + عدّ كامل + Skip/Take.</summary>
        public async Task<PagedResult<Supplier>> GetSuppliersPagedAsync(string search = "", bool includeInactive = false, int page = 1, int pageSize = 20)
        {
            page = Math.Max(1, page);
            pageSize = Math.Max(1, pageSize);

            var query = _dbContext.Suppliers.Include(c => c.Party)
                .Where(c => string.IsNullOrWhiteSpace(search) || c.Party.Name.Contains(search) || c.Party.Email.Contains(search));

            if (!includeInactive)
                query = query.Where(c => c.Party.IsActive);

            int total = await query.CountAsync();
            var items = await query
                .OrderBy(c => c.Party.Name)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return new PagedResult<Supplier> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
        }

        public async Task<Supplier?> GetSupplierByIdAsync(int id)
        {
            var query = _dbContext.Suppliers.Include(s => s.Party);

            //IQueryable<DtoCustomer> customersQuery = CustomerQueryProjection(query);

            Supplier? supplier = await query.SingleOrDefaultAsync(c => c.PartyId == id);

            return supplier;
        }
      
    }
}
