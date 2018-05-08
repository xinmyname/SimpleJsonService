using SimpleJsonService;

namespace Example
{
    public class ExampleController : JsonControllerBase
    {
        public string Ping()
        {
            return "Pong!";
        }

        public string WhoAmI()
        {
            return Context.User?.Identity.Name ?? "<Anonymous>";
        }
    }
}
