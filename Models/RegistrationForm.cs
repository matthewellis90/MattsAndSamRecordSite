using System.ComponentModel.DataAnnotations;

namespace MattsRecords.Models;

public sealed class RegistrationForm
{
    [Required, RegularExpression(@"[A-Za-z0-9][A-Za-z0-9_.-]{2,31}",
        ErrorMessage = "Use 3 to 32 letters, numbers, dots, underscores, or hyphens. Start with a letter or number.")]
    public string Username { get; set; } = "";

    [Required, StringLength(254), EmailAddress, Display(Name = "Email address")]
    public string Email { get; set; } = "";

    [Required, StringLength(256, MinimumLength = 12), DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [Required, Compare(nameof(Password)), Display(Name = "Confirm password"), DataType(DataType.Password)]
    public string Confirmation { get; set; } = "";
}
