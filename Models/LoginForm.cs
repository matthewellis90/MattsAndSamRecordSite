using System.ComponentModel.DataAnnotations;

namespace MattsRecords.Models;

public sealed class LoginForm
{
    [Required, StringLength(100)]
    public string Username { get; set; } = "";

    [Required, StringLength(256), DataType(DataType.Password)]
    public string Password { get; set; } = "";

    public string? ReturnUrl { get; set; }
}
