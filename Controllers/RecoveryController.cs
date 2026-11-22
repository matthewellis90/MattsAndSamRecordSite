using MattsRecords.Models;
using MattsRecords.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MattsRecords.Controllers;

[Route("Account")]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class RecoveryController(IAccountRecovery recovery, IAccountMail mail) : Controller
{
    public const string OwnerPolicy = "collection-owner";
    public const string RecoveryRateLimit = "account-recovery";

    [AllowAnonymous, HttpGet("Forgot")]
    public IActionResult Forgot() => View(new RecoveryRequestForm());

    [AllowAnonymous, HttpPost("Forgot")]
    [EnableRateLimiting(RecoveryRateLimit)]
    public async Task<IActionResult> Forgot(RecoveryRequestForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return View(form);
        await recovery.RequestAsync(form.Username, mail.Configured, cancellationToken);
        return RedirectToAction(nameof(Requested));
    }

    [AllowAnonymous, HttpGet("Requested")]
    public IActionResult Requested() => View(mail.Configured);

    [Authorize(Policy = OwnerPolicy), HttpGet("Recovery")]
    public async Task<IActionResult> Manage(CancellationToken cancellationToken) =>
        View(new RecoveryQueue(await recovery.ListAsync(cancellationToken)));

    [Authorize(Policy = OwnerPolicy), HttpPost("Recovery/Issue")]
    public async Task<IActionResult> Issue(Guid accountId, CancellationToken cancellationToken)
    {
        var requests = await recovery.ListAsync(cancellationToken);
        var account = requests.SingleOrDefault(request => request.AccountId == accountId);
        if (account is null)
            return NotFound();
        var token = await recovery.IssueAsync(accountId, cancellationToken);
        if (token is null)
            return RedirectToAction(nameof(Manage));

        var address = Url.Action(nameof(Reset), "Recovery", values: null, protocol: Request.Scheme);
        return View(nameof(Manage), new RecoveryQueue(await recovery.ListAsync(cancellationToken), address + "#" + token, account.Username));
    }

    [AllowAnonymous, HttpGet("Reset")]
    public IActionResult Reset()
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return View(new ResetPasswordForm());
    }

    [AllowAnonymous, HttpPost("Reset")]
    [EnableRateLimiting(RecoveryRateLimit)]
    public async Task<IActionResult> Reset(ResetPasswordForm form, CancellationToken cancellationToken)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        if (!ModelState.IsValid)
            return View(form);
        if (!await recovery.ResetAsync(form.Token, form.Password, cancellationToken))
        {
            ModelState.AddModelError(string.Empty, "This link is invalid or has expired. Request a new one.");
            return View(form);
        }
        TempData["Status"] = "Password updated. Sign in with your new password.";
        return RedirectToAction("Login", "Admin");
    }
}
