#include "app_server/middleware.cuh"

namespace app_server::middleware {

static_assert(Middleware<LoggingMiddleware>, "LoggingMiddleware must satisfy the Middleware concept");
static_assert(Middleware<AuthMiddleware>, "AuthMiddleware must satisfy the Middleware concept");
static_assert(Middleware<RateLimitMiddleware>, "RateLimitMiddleware must satisfy the Middleware concept");
static_assert(Middleware<CORSMiddleware>, "CORSMiddleware must satisfy the Middleware concept");
static_assert(Middleware<TimingMiddleware>, "TimingMiddleware must satisfy the Middleware concept");

}  // namespace app_server::middleware
