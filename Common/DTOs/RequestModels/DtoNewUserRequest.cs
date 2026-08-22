using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.RequestModels
{
    public record NewUserRequest(
        string userName,
        string password,
        byte RoleId,
        int? EmployeeId
        );
}
