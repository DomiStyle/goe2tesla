using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Goe2Tesla
{
    public class TeslaApiException : Exception
    {
        public HttpStatusCode Status { get; private set; }
        public string Response { get; private set; }

        public TeslaApiException(string message, HttpStatusCode status, string response) : base(message)
        {
            this.Status = status;
            this.Response = response;
        }

        public TeslaApiException(string message) : base(message)
        {
        }

        public TeslaApiException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
