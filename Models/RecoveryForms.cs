using System.ComponentModel.DataAnnotations;

namespace MattsRecords.Models;

public sealed class RecoveryRequestForm
{
    [Required, StringLength(32)]
    public string Username { get; set; } = "";
}

public sealed class ResetPasswordForm
{
    [Required(ErrorMessage = "Open the complete reset link from the site owner."), StringLength(43, MinimumLength = 43)]
    public string Token { get; set; } = "";

    [Required, StringLength(256, MinimumLength = 12), DataType(DataType.Password), Display(Name = "New password")]
    public string Password { get; set; } = "";

    [Required, StringLength(256), Compare(nameof(Password)), DataType(DataType.Password), Display(Name = "Confirm new password")]
    public string Confirmation { get; set; } = "";
}

public sealed record RecoveryRequest(Guid AccountId, string Username, bool HasActiveLink);

public sealed record RecoveryQueue(IReadOnlyList<RecoveryRequest> Requests, string? Link = null, string? Username = null);
