using Common.Enums;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;

namespace Common.Utils
{
    public static class clsValidation
    {
        public static string ValidateString(string text, string varibleName, int length = -1)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ValidationException($"{varibleName} is required");

            text = text.Trim();

            if (length > 0)
            {
                if (text.Length < 2 || text.Length > length)
                    throw new ValidationException("Invalid name length");
            }

            return text;
        }

        public static string ValidateEmail(string email, string variableName = "Email")
        {
            if (string.IsNullOrWhiteSpace(email))
                throw new ValidationException($"{variableName} is required.");

            email = email.Trim().ToLowerInvariant();

            if (email.Length > 254)
                throw new ValidationException($"{variableName} is too long.");

            try
            {
                var mail = new MailAddress(email);
                if (!string.Equals(mail.Address, email, StringComparison.Ordinal))
                    throw new ValidationException($"Invalid {variableName}.");
            }
            catch
            {
                throw new ValidationException($"Invalid {variableName}.");
            }

            return email;
        }

        public static string? ValidateUrl(string? url, string variableName = "Url", bool required = false, int maxLength = 500)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                if (required)
                    throw new ValidationException($"{variableName} is required.");

                return null;
            }

            url = url.Trim();

            if (url.Length > maxLength)
                throw new ValidationException($"{variableName} is too long.");

            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                throw new ValidationException($"Invalid {variableName}.");

            return url;
        }

        public static decimal ValidatePrice(decimal price, string variableName = "Price", decimal minValue = 0.01m, decimal maxValue = 1000000m)
        {
            if (price < minValue)
                throw new ValidationException($"{variableName} must be greater than or equal to {minValue}.");

            if (price > maxValue)
                throw new ValidationException($"{variableName} is too large.");

            return price;
        }

        public static int ValidatePositiveInt(int value, string variableName = "Value", int minValue = 1)
        {
            
            if (value < minValue)
                throw new ValidationException($"{variableName} must be greater than or equal to {minValue}.");

            return value;
        }

        public static DateTime ValidateDateInRange(DateTime date, string variableName, DateTime minDate, DateTime maxDate)
        {
            if (date < minDate || date > maxDate)
                throw new ValidationException($"Invalid {variableName}.");

            return date;
        }

        public static DateTime ValidateBirthDate(DateTime birthDate, int minAgeYears = 10, int minYear = 1930)
        {
            var minDate = new DateTime(minYear, 1, 1);
            var maxDate = DateTime.UtcNow.AddYears(-minAgeYears);

            if (birthDate < minDate || birthDate > maxDate)
                throw new ValidationException("Invalid BirthDate.");

            return birthDate;
        }

        public static string ValidatePassword(
            string password,
            string variableName = "Password",
            int minLength = 12,
            int maxLength = 128,
            bool requireUpper = true,
            bool requireLower = true,
            bool requireDigit = true)
        {
            if (string.IsNullOrWhiteSpace(password))
                throw new ValidationException($"{variableName} is required.");

            password = password.Trim();

            if (password.Length < minLength)
                throw new ValidationException($"{variableName} must be at least {minLength} characters.");

            if (password.Length > maxLength)
                throw new ValidationException($"{variableName} is too long.");

            if (requireUpper && !Regex.IsMatch(password, "[A-Z]"))
                throw new ValidationException($"{variableName} must contain at least one uppercase letter.");

            if (requireLower && !Regex.IsMatch(password, "[a-z]"))
                throw new ValidationException($"{variableName} must contain at least one lowercase letter.");

            if (requireDigit && !Regex.IsMatch(password, "[0-9]"))
                throw new ValidationException($"{variableName} must contain at least one digit.");

            return password;
        }

        
    }
   
}


