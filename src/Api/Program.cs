// Program.cs registration pattern matches digital-wallet.
// Services: IRideService, IMatchingService, IDriverLocationService
// Middleware: GlobalExceptionHandler, RequestLoggingMiddleware, JWT auth
// Rate limiting: PATCH /drivers/{id}/location at 30/min per driver