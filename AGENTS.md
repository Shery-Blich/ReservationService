# Coding Standards — ReservationService

These are the coding standards for this repo. It's an ASP.NET Core Web API (net10.0, `Nullable` and `ImplicitUsings` enabled). These standards assume C# 11+ language features are available.

---

## 1. Naming Conventions

| Element | Convention | Example |
|---|---|---|
| Methods / Functions | PascalCase | `CreateReservation()`, `CancelBooking()` |
| Public Properties | PascalCase | `IsConfirmed`, `GuestCount` |
| Private / local fields | camelCase | `reservationId`, `cancellationCount` |
| Constants | PascalCase | `MaxPartySize`, `DefaultTimeoutSeconds` |
| Interfaces | `I` prefix, PascalCase | `IReservationService`, `INotifier` |

---

## 2. Field Declaration & Encapsulation

- Prefer auto-properties over manually backed fields.
- Use `{ get; private set; }` for properties that are read-only to other systems but writable internally.
- Avoid public mutable fields. Expose state through properties.

```csharp
// GOOD
public string GuestName { get; private set; }

// BAD
public string guestName;
```

---

## 3. Code Style — `var` and Spacing

- **Always prefer `var`** on the left-hand side of a declaration when the type is clear from the right-hand side:

```csharp
// GOOD
var reservation = GetReservation(id);
var guests = new List<Guest>();
var distanceInDays = (arrival - today).Days;

// BAD
Reservation reservation = GetReservation(id);
List<Guest> guests = new List<Guest>();
int distanceInDays = (arrival - today).Days;
```

- Only use an explicit type when `var` would genuinely obscure what the variable holds (e.g. return types of opaque factory methods).

- **Spacing rules:**
  - One blank line between every method.
  - One blank line between logically distinct blocks inside a method.
  - No trailing whitespace.
  - Opening braces `{` always on their own line (Allman style).
  - Always use braces even for single-line `if` bodies.

```csharp
// GOOD
if (isConfirmed)
{
    SendConfirmationEmail();
}

// BAD
if (isConfirmed) SendConfirmationEmail();
```

---

## 4. Project Structure

- Keep endpoint handlers (minimal API route delegates, or controller actions) as thin shells. They should parse/validate the request, call into an injected service, and shape the response — no business logic inline.
- All real logic lives in plain C# service classes that are injected in. This keeps logic unit-testable without spinning up the web host.

```csharp
// BAD — logic directly in the endpoint delegate
app.MapPost("/reservations", (ReservationRequest request) =>
{
    if (request.PartySize > 12) { /* ...20 lines of validation and booking logic... */ }
});

// GOOD — endpoint delegates to injected service
app.MapPost("/reservations", (ReservationRequest request, IReservationService reservations) =>
    reservations.Create(request));
```

---

## 5. SOLID & DRY Principles

- **Single Responsibility**: Every class and every method does exactly one thing. If you find yourself writing "and" to describe a method's purpose, split it.
- **Open/Closed**: Prefer extending behaviour via interfaces and composition rather than modifying existing classes.
- **Liskov Substitution**: Subtypes must be usable in place of their base type without breaking behaviour.
- **Interface Segregation**: Prefer small, focused interfaces over large, monolithic ones.
- **Dependency Inversion**: Depend on abstractions (interfaces), not concrete classes. Inject dependencies rather than instantiating them internally.
- **DRY**: If the same logic appears more than once, extract it into a shared helper or service. Never duplicate code across classes.

---

## 6. Dependency Injection

- Use ASP.NET Core's built-in DI container (`Microsoft.Extensions.DependencyInjection`). Do not introduce a third-party DI framework.
- Inject dependencies via constructor injection:

```csharp
public class ReservationService : IReservationService
{
    private readonly IReservationRepository repository;

    public ReservationService(IReservationRepository repository)
    {
        this.repository = repository;
    }
}
```

- Register services in `Program.cs`, binding to interfaces over concrete types:

```csharp
builder.Services.AddScoped<IReservationService, ReservationService>();
```

- Choose lifetimes deliberately: `Transient`, `Scoped`, or `Singleton` — default to `Scoped` for services that touch per-request state (e.g. a database context).

---

## 7. Modern C# Language Features (C# 11+)

Prefer modern language features over older equivalents:

- **Records** for immutable data-carrying types (`record`, `record struct`).
- **Required members** (`required` modifier) instead of throwing in a constructor for mandatory properties.
- **Raw string literals** (`"""..."""`) for multi-line or quote-heavy strings.
- **List patterns** (`[first, .. rest]`) and enhanced pattern matching over manual index checks.
- **File-scoped namespaces** (`namespace Foo;`) instead of block-scoped namespaces.
- **Primary constructors** for simple classes where they reduce boilerplate without hiding meaningful logic.
- **Collection expressions** (`[1, 2, 3]`) instead of verbose collection initializers.
- **Generic math / static abstract interface members** where numeric-generic code is needed.

---

## 8. Self-Documenting Code

Extract any non-trivial logic out of handlers/lifecycle methods into single-purpose, descriptively named private helpers. The method name itself should explain the *why*, not just the *what*:

```csharp
// BAD — magic inline logic
var fee = Math.Max(basePrice * (1 - partySize * 0.02m), basePrice * 0.5m);

// GOOD — extracted, self-documenting
private decimal ApplyGroupDiscount(decimal basePrice, int partySize)
    => Math.Max(basePrice * (1 - partySize * 0.02m), basePrice * 0.5m);
```

---

## 9. Comments Policy

- Do not add comments, TODOs, or XML `<summary>` blocks when writing or editing code.
- If a design decision genuinely seems to need an explanatory comment (a non-obvious constraint, a workaround, a subtle invariant), stop and discuss it with the user first. Never add the comment and continue on your own.
