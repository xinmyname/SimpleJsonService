using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SimpleJsonService;

namespace Example
{
    internal static class Program
    {
        private static void Main()
        {
            var tokenSource = new CancellationTokenSource();

            Console.CancelKeyPress += (s,e) => {
                tokenSource.Cancel();
                e.Cancel = true;
            };

            var serviceHost = JsonServiceHost.Create<ExampleController>("http://localhost:38080/", AuthenticationSchemes.Negotiate);

            try
            {
                serviceHost.Start();
                Task.Delay(-1, tokenSource.Token).Wait(tokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                serviceHost.Stop();
            }
        }
    }
}
