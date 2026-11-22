using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace MattsRecords.Models;

public sealed class AccountEmailForm
{
    [Display(Name = "Email address"), Required, EmailAddress, StringLength(254)]
    public string Address { get; set; } = "";

    [Display(Name = "Current password"), Required, StringLength(256), DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [BindNever]
    public string? Verified { get; set; }
    [BindNever]
    public string? Pending { get; set; }
    [BindNever]
    public bool MailConfigured { get; set; }
}

public sealed class ConfirmEmailForm
{
    [Required(ErrorMessage = "Open the complete verification link from your email."), StringLength(43, MinimumLength = 43)]
    public string Token { get; set; } = "";
}
