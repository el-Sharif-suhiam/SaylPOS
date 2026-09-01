using Common.DTOs;
using Common.Exceptions;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;
using Common.Enums;

namespace Sayl.Core.Services
{
    public class clsEmployeeService
    {
        private readonly SaylDbContext _dbContext;

        public clsEmployeeService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        private static readonly string[] ValidStatuses = { "active", "on_leave", "resigned" };

        /// <summary>قائمة الموظفين كـ DTOs (بدلاً من كيانات EF) مع بحث اختياري.</summary>
        public async Task<List<DtoEmployee>> GetEmployeesListAsync(string search = "", bool includeInactive = true)
        {
            var query = _dbContext.Employees.AsQueryable();

            if (!includeInactive)
                query = query.Where(e => e.Status == "active");

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(e => e.Name.Contains(search)
                                       || e.Position.Contains(search)
                                       || (e.Department != null && e.Department.Contains(search)));

            return await query
                .OrderBy(e => e.Name)
                .Select(e => new DtoEmployee
                {
                    EmployeeId = e.EmployeeId,
                    Name = e.Name,
                    Phone = e.Phone,
                    Email = e.Email,
                    Salary = e.Salary,
                    SalaryType = e.SalaryType,
                    HireDate = e.HireDate,
                    ResignDate = e.ResignDate,
                    Address = e.Address,
                    Status = e.Status,
                    Position = e.Position,
                    Department = e.Department,
                    CreatedAt = e.CreatedAt
                })
                .ToListAsync();
        }

        public async Task<DtoEmployee?> GetEmployeeByIdAsync(int id)
        {
            var e = await _dbContext.Employees.FindAsync(id);
            if (e == null) return null;

            return new DtoEmployee
            {
                EmployeeId = e.EmployeeId,
                Name = e.Name,
                Phone = e.Phone,
                Email = e.Email,
                Salary = e.Salary,
                SalaryType = e.SalaryType,
                HireDate = e.HireDate,
                ResignDate = e.ResignDate,
                Address = e.Address,
                Status = e.Status,
                Position = e.Position,
                Department = e.Department,
                CreatedAt = e.CreatedAt
            };
        }

        public async Task<Result<int>> AddNewEmployeeAsync(DtoEmployee dtoEmployee)
        {
            if (string.IsNullOrWhiteSpace(dtoEmployee.Name))
                return Result<int>.Fail("الاسم مطلوب.");

            if (string.IsNullOrWhiteSpace(dtoEmployee.Position))
                return Result<int>.Fail("المسمى الوظيفي مطلوب.");

            if (string.IsNullOrWhiteSpace(dtoEmployee.Status) || !ValidStatuses.Contains(dtoEmployee.Status.ToLower()))
                return Result<int>.Fail("حالة الموظف غير صالحة.");

            if (dtoEmployee.Salary.HasValue && dtoEmployee.Salary.Value < 0)
                return Result<int>.Fail("الراتب يجب أن يكون صفراً أو أكثر.");

            var employee = new Employee
            {
                Name = clsValidation.ValidateString(dtoEmployee.Name, "Name"),
                Phone = string.IsNullOrWhiteSpace(dtoEmployee.Phone) ? null : dtoEmployee.Phone,
                Email = string.IsNullOrWhiteSpace(dtoEmployee.Email) ? null : dtoEmployee.Email,
                Salary = dtoEmployee.Salary,
                SalaryType = dtoEmployee.SalaryType,
                HireDate = dtoEmployee.HireDate,
                ResignDate = dtoEmployee.ResignDate,
                Address = string.IsNullOrWhiteSpace(dtoEmployee.Address) ? null : dtoEmployee.Address,
                Status = dtoEmployee.Status.ToLower(),
                Position = clsValidation.ValidateString(dtoEmployee.Position, "Position"),
                Department = string.IsNullOrWhiteSpace(dtoEmployee.Department) ? null : dtoEmployee.Department
            };

            _dbContext.Employees.Add(employee);
            bool saved = (await _dbContext.SaveChangesAsync()) > 0;
            return saved ? Result<int>.Ok(employee.EmployeeId, "تمت إضافة الموظف بنجاح.") : Result<int>.Fail("فشلت الإضافة.");
        }

        public async Task<Result> UpdateEmployeeAsync(DtoEmployee dtoEmployee)
        {
            if (dtoEmployee.EmployeeId <= 0)
                return Result.Fail("معرف الموظف غير صالح.");

            Employee? employee = await _dbContext.Employees.FindAsync(dtoEmployee.EmployeeId);
            if (employee == null)
                return Result.Fail("الموظف غير موجود.");

            if (!string.IsNullOrWhiteSpace(dtoEmployee.Name))
                employee.Name = clsValidation.ValidateString(dtoEmployee.Name, "Name");

            if (!string.IsNullOrWhiteSpace(dtoEmployee.Phone))
                employee.Phone = dtoEmployee.Phone;

            if (!string.IsNullOrWhiteSpace(dtoEmployee.Email))
                employee.Email = clsValidation.ValidateEmail(dtoEmployee.Email);

            if (dtoEmployee.Salary.HasValue)
            {
                if (dtoEmployee.Salary.Value < 0)
                    return Result.Fail("الراتب يجب أن يكون صفراً أو أكثر.");
                employee.Salary = dtoEmployee.Salary.Value;
            }

            employee.SalaryType = dtoEmployee.SalaryType;

            if (dtoEmployee.HireDate.HasValue)
                employee.HireDate = dtoEmployee.HireDate.Value;

            if (!string.IsNullOrWhiteSpace(dtoEmployee.Address))
                employee.Address = dtoEmployee.Address;

            if (!string.IsNullOrWhiteSpace(dtoEmployee.Status))
            {
                string newStatus = dtoEmployee.Status.ToLower();
                if (!ValidStatuses.Contains(newStatus))
                    return Result.Fail("حالة الموظف غير صالحة.");

                // تاريخ الاستقالة يُشتق من الحالة: «مستقيل» يسجّل اليوم (أو التاريخ المحدد)،
                // والعودة لنشط/إجازة تمسحه — Proration يقطع عنده في الرواتب الشهرية
                if (newStatus == "resigned")
                {
                    employee.Status = newStatus;
                    employee.ResignDate ??= dtoEmployee.ResignDate ?? DateOnly.FromDateTime(DateTime.Today);
                }
                else
                {
                    employee.Status = newStatus;
                    employee.ResignDate = null;
                }
            }
            else if (dtoEmployee.ResignDate.HasValue)
            {
                employee.ResignDate = dtoEmployee.ResignDate.Value;
            }

            if (!string.IsNullOrWhiteSpace(dtoEmployee.Position))
                employee.Position = clsValidation.ValidateString(dtoEmployee.Position, "Position");

            if (!string.IsNullOrWhiteSpace(dtoEmployee.Department))
                employee.Department = dtoEmployee.Department;

            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تحديث بيانات الموظف بنجاح.");
        }

        // تعطيل / تفعيل موظف (يمكن دمجه مع الحذف الناعم)
        public async Task<Result> SetEmployeeStatusAsync(int employeeId, string status)
        {
            if (!ValidStatuses.Contains(status.ToLower()))
                return Result.Fail("حالة غير صالحة.");

            Employee? employee = await _dbContext.Employees.FindAsync(employeeId);
            if (employee == null)
                return Result.Fail("الموظف غير موجود.");

            employee.Status = status.ToLower();
            await _dbContext.SaveChangesAsync();
            return Result.Ok($"تم تغيير حالة الموظف إلى {status}.");
        }
    }
}
