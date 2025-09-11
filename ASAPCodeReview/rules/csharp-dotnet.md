# .NET Development Rules

  ## Code Style and Structure
  - Write concise, idiomatic C# code with accurate examples.
  - Follow .NET and ASP.NET Core conventions and best practices.
  - Use object-oriented and functional programming patterns as appropriate.
  - Prefer LINQ and lambda expressions for collection operations.
  - Use descriptive variable and method names (e.g., `IsUserSignedIn`, `CalculateTotal`).
  - Structure files according to .NET conventions (Controllers, Models, Services, etc.).

  ## Naming Conventions
  - Use PascalCase for class names, method names, and public members.
  - Use camelCase for local variables and private fields.
  - Use UPPERCASE for constants.
  - Prefix interface names with "I" (e.g., `IUserService`).

  ## C# and .NET Usage
  - Use C# 10+ features when appropriate (e.g., record types, pattern matching, null-coalescing assignment).
  - Leverage built-in ASP.NET Core features and middleware.
  - Use Entity Framework Core effectively for database operations.

  ## Syntax and Formatting
  - Follow the C# Coding Conventions (https://docs.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
  - Use C#'s expressive syntax (e.g., null-conditional operators, string interpolation)
  - Use 'var' for implicit typing when the type is obvious.

  ## Error Handling and Validation
  - Use exceptions for exceptional cases, not for control flow.
  - Implement proper error logging using built-in .NET logging or a third-party logger. We typically shall use SeriLog with Logging to Console!
  - Use Data Annotations or Fluent Validation for model validation.
  - Implement global exception handling middleware.
  - Return appropriate HTTP status codes and consistent error responses.

  ## API Design
  - Follow RESTful API design principles.
  - Use attribute routing in controllers.
  - Implement versioning for your API.
  - Use action filters for cross-cutting concerns.

  ## Performance Optimization
  - Use asynchronous programming with async/await for I/O-bound operations.
  - Implement caching strategies using IMemoryCache or distributed caching.
  - Use efficient LINQ queries and avoid N+1 query problems.
  - Implement pagination for large data sets.

  ## Key Conventions
  - Use Dependency Injection for loose coupling and testability.
  - Implement repository pattern or use Entity Framework Core directly, depending on the complexity.
  - Use AutoMapper for object-to-object mapping if needed.
  - Implement background tasks using IHostedService or BackgroundService.

  ## Testing
  - xUnit  for unit testing
  - Moq for mocking (or NSubstitute as an alternative)
  - AwesomeAssertions (fork of FluentAssertions) for validations
  - Respawn for database cleanup in integration tests
  - TestContainers for containerized testing
  - wiremock.net  for HTTP mocking

  ## Security
  - Use Authentication and Authorization middleware.
  - Implement JWT authentication for stateless API authentication.
  - Use HTTPS and enforce SSL.
  - Implement proper CORS policies.

  ## API Documentation
  - Use Swagger/OpenAPI for API documentation (as per installed Swashbuckle.AspNetCore package).
  - Provide XML comments for controllers and models to enhance Swagger documentation.

  ## Workflow and Development Environment
  - Code editing, AI suggestions, and refactoring will be done within Cursor AI.
  - Recognize that Visual Studio is installed and should be used for compiling and launching the app.

  ## Naming Conventions
  - Follow PascalCase for component names, method names, and public members.
  - Use camelCase for private fields and local variables.
  - Prefix interface names with "I" (e.g., `IUserService`).

  ## API Design and Integration
  - Use REFIT library to communicate with external APIs or your own backend.
  - Implement error handling for API calls using try-catch and provide proper user feedback in the UI.

  ## Testing and Debugging in Visual Studio
  - All unit testing and integration testing should be done in Visual Studio Professional.
  - Use Moq or NSubstitute for mocking dependencies during tests.
  - For performance profiling and optimization, rely on Visual Studio's diagnostics tools.

  ## Security and Authentication
  - Use HTTPS for all web communication and ensure proper CORS policies are implemented.

  ## API Documentation and Swagger
  - Use Swagger/OpenAPI for API documentation for your backend API services.
  - Ensure XML documentation for models and API methods for enhancing Swagger documentation.
  
Follow the official Microsoft documentation and ASP.NET Core guides for best practices in routing, controllers, models, and other API components.
