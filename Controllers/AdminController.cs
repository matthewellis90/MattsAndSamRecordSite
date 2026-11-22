using MattsRecords.Models;
using MattsRecords.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MimeKit;

namespace MattsRecords.Controllers;

[Authorize]
[Route("Admin")]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class AdminController(AdminAuthentication authentication, ICollectionAccounts accounts, IAccountMail mail,
    ILogger<AdminController> logger) : Controller
{
    public const string RegistrationRateLimit = "account-registration";

    [AllowAnonymous]
    [HttpGet("Login")]
    public IActionResult Login(string? returnUrl)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectAfterLogin(returnUrl);

        return View(new LoginForm { ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null });
    }

    [AllowAnonymous]
    [HttpPost("Login")]
    [EnableRateLimiting(AdminAuthentication.LoginRateLimit)]
    public async Task<IActionResult> Login(LoginForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return View(form);

        var principal = authentication.SignIn(form.Username, form.Password);
        if (principal is null && !authentication.IsOwnerName(form.Username))
        {
            var account = await accounts.FindByNameAsync(form.Username, cancellationToken);
            if (account is not null && accounts.Verify(account, form.Password))
                principal = CollectionSession.Create(account);
        }
        if (principal is null)
        {
            ModelState.AddModelError(string.Empty, "The username or password is incorrect.");
            form.Password = "";
            ModelState.Remove(nameof(form.Password));
            return View(form);
        }

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        if (CollectionSession.IsPersonal(principal) && !CollectionSession.IsReady(principal))
            return RedirectToAction(nameof(Preparing));
        return RedirectAfterLogin(form.ReturnUrl);
    }

    [AllowAnonymous, HttpGet("Register")]
    public IActionResult Register() => User.Identity?.IsAuthenticated == true
        ? RedirectToAction("Index", "Home") : View(new RegistrationForm());

    [AllowAnonymous, HttpPost("Register")]
    [EnableRateLimiting(RegistrationRateLimit)]
    public async Task<IActionResult> Register(RegistrationForm form, CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");
        if (!ModelState.IsValid)
            return View(form);

        if (authentication.IsOwnerName(form.Username))
        {
            ModelState.AddModelError(nameof(form.Username), "Choose a different username.");
            return View(form);
        }

        if (!mail.Configured)
        {
            ModelState.AddModelError(string.Empty, "Sign-up is awaiting email setup by the site owner.");
            return View(form);
        }
        var address = form.Email.Trim();
        if (!MailboxAddress.TryParse(address, out var mailbox) || mailbox.Address != address)
        {
            ModelState.AddModelError(nameof(form.Email), "Enter a single email address.");
            return View(form);
        }

        var (result, account, code) = await accounts.RegisterAsync(form.Username, form.Password, address, cancellationToken);
        if (result != RegistrationResult.Created)
        {
            ModelState.AddModelError(string.Empty, result == RegistrationResult.UsernameTaken
                ? "That username is taken." : "New accounts are currently at capacity.");
            return View(form);
        }

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, CollectionSession.Create(account));
        try
        {
            await mail.SendAsync(address, "Confirm your Record Room registration",
                "Confirm your email address to activate your account and create your private collection. This link expires in 24 hours.",
                "/Account/VerifyEmail", code, cancellationToken);
            TempData["Status"] = "Confirmation email sent.";
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Registration email delivery failed ({ErrorType}).", error.GetType().Name);
            TempData["Status"] = "The site couldn’t send your confirmation email. Use the link below to try again in a minute.";
        }
        return RedirectToAction(nameof(Preparing));
    }

    [HttpGet("Preparing")]
    public IActionResult Preparing()
    {
        if (!CollectionSession.IsPersonal(User) || CollectionSession.IsReady(User))
            return RedirectToAction("Index", "Home");
        return View(CollectionSession.HasFailed(User));
    }

    [HttpPost("Logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Index", "Home");
    }

    private IActionResult RedirectAfterLogin(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToAction("Index", "Home");
}
