using Newtonsoft.Json;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Net.Mqtt;
using System.Reactive.Linq;

namespace Goe2Tesla
{
    class Program
    {
        private readonly string userAgent = "Goe2Tesla/1.2";
        private readonly string clientId = "81527cff06843c8634fdc09e8ac0abefb46ac849f38fe1e431c2ef2106796384";
        private readonly string clientSecret = "c7257eb71a564034f9419ee651c7d0e5f7aa6bfbd18bafb5c5c033b093bb2fa3";

        private readonly string tokenFile = Environment.GetEnvironmentVariable("TOKEN_FILE");

        private readonly string teslaEmail = Environment.GetEnvironmentVariable("TESLA_EMAIL");
        private readonly string teslaPassword = Environment.GetEnvironmentVariable("TESLA_PASSWORD");
        private readonly string teslaVehicleVin = Environment.GetEnvironmentVariable("TESLA_VEHICLE_VIN");

        private readonly string mqttServer = Environment.GetEnvironmentVariable("MQTT_SERVER");
        private readonly int mqttPort = int.Parse(Environment.GetEnvironmentVariable("MQTT_PORT"));
        private readonly string mqttUsername = Environment.GetEnvironmentVariable("MQTT_USERNAME");
        private readonly string mqttPassword = Environment.GetEnvironmentVariable("MQTT_PASSWORD");
        private readonly string mqttTopic = Environment.GetEnvironmentVariable("MQTT_TOPIC");

        private string accessToken = null;
        private string refreshToken = null;
        private DateTimeOffset validUntil;

        private bool previousState;
        private bool initialized = false;
        private bool working = false;

        private static ManualResetEvent ResetEvent { get; } = new ManualResetEvent(false);

        static void Main(string[] args)
        {
            Program p = new Program();
            p.Run();

            Program.ResetEvent.WaitOne();
        }

        private async void Run()
        {
            Console.WriteLine("Initializing");

            string clientName = "Goe2Tesla " + Dns.GetHostName().Where(c => Char.IsLetterOrDigit(c)).ToString();

            var mqttClient = await MqttClient.CreateAsync(mqttServer, mqttPort);
            await mqttClient.ConnectAsync(new MqttClientCredentials(clientName, mqttUsername, mqttPassword), cleanSession: true);

            mqttClient.MessageStream.Where(message => message.Topic == mqttTopic).Subscribe(async message =>
            {
                if (this.working)
                {
                    return;
                }

                dynamic json = JsonConvert.DeserializeObject(Encoding.UTF8.GetString(message.Payload));

                bool currentState = this.ParseBool(json.alw.ToString());

                if (this.initialized)
                {
                    if (currentState && currentState != previousState)
                    {
                        Console.WriteLine("Charging enabled, waking up car...");
                        this.working = true;

                        await this.HandleWakeUp();

                        this.working = false;
                        Console.WriteLine("Woke up car");
                    }
                }
                else
                {
                    this.initialized = true;
                }

                this.previousState = currentState;
            });

            Console.WriteLine("Connected to server");

            await mqttClient.SubscribeAsync(mqttTopic, MqttQualityOfService.ExactlyOnce);

            Console.WriteLine("Subscribed to topic");

            mqttClient.Disconnected += (object sender, MqttEndpointDisconnected e) =>
            {
                Console.WriteLine("MQTT connection lost");
                Program.ResetEvent.Set();
            };

            await this.HandleToken();
        }

        private bool ParseBool(string input)
        {
            return (input == "1");
        }

        private async Task HandleWakeUp(ushort retries = 0)
        {
            if(retries > 3)
            {
                Console.WriteLine("Retries exceeded, waiting for user interaction");
                Program.ResetEvent.WaitOne();
            }

            try
            {
                Console.WriteLine("Handling token");
                await this.HandleToken();

                Console.WriteLine("Getting vehicle id");
                string vehicleId = await this.GetVehicleId();

                while (true)
                {
                    Console.WriteLine("Waking up...");
                    await this.WakeUp(vehicleId);

                    Console.WriteLine("Waiting for vehicle to come online...");
                    await Task.Delay(60000);

                    string status = await this.GetVehicleStatus();

                    if(status == "online")
                    {
                        Console.WriteLine("Vehicle is online");
                        break;
                    }
                    else
                    {
                        Console.WriteLine("Expected vehicle to be online but got " + status + ", retrying...");
                    }
                }
            }
            catch (TeslaApiException ex)
            {
                Console.WriteLine("-------------------------");
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.Status);
                Console.WriteLine("-------------------------");
                Console.WriteLine(ex.Response);
                Console.WriteLine("-------------------------");

                Console.WriteLine("Clearing token and retrying in 15 seconds");

                await Task.Delay(TimeSpan.FromSeconds(15));

                this.ClearToken();
                retries++;
                await this.HandleWakeUp(retries);
            }
        }

        private async Task HandleToken()
        {
            if (this.accessToken == null && File.Exists(tokenFile))
            {
                Console.WriteLine("Reading token from token file");
                this.ParseToken(JsonConvert.DeserializeObject(await File.ReadAllTextAsync(tokenFile)));
            }

            if (this.accessToken == null)
            {
                Console.WriteLine("Logging into Tesla API...");

                await this.Login();

                Console.WriteLine("Logged into API");
            }
            else if (DateTimeOffset.Now >= this.validUntil)
            {
                Console.WriteLine("Refreshing Tesla token...");

                await this.Refresh();

                Console.WriteLine("Refreshed token");
            }
        }

        private async Task<string> GetVehicleStatus()
        {
            dynamic json = JsonConvert.DeserializeObject(await this.GetVehicleList());

            foreach (dynamic vehicleJson in json.response)
            {
                if (vehicleJson.vin.ToString() == this.teslaVehicleVin)
                    return vehicleJson.state.ToString();
            }

            return null;
        }

        private async Task<string> GetVehicleId()
        {
            dynamic json = JsonConvert.DeserializeObject(await this.GetVehicleList());

            foreach (dynamic vehicleJson in json.response)
            {
                if (vehicleJson.vin.ToString() == this.teslaVehicleVin)
                    return vehicleJson.id.ToString();
            }

            return null;
        }

        private async Task<string> GetVehicleList()
        {
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + this.accessToken);

                using (HttpResponseMessage response = await client.GetAsync("https://owner-api.teslamotors.com/api/1/vehicles"))
                {
                    string responseString = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new TeslaApiException("Get vehicle list failed", response.StatusCode, responseString);

                    return responseString;
                }
            }
        }

        private async Task WakeUp(string vehicleId)
        {
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + this.accessToken);

                using (HttpResponseMessage response = await client.PostAsync(string.Format("https://owner-api.teslamotors.com/api/1/vehicles/{0}/wake_up", vehicleId), new StringContent("")))
                {
                    string json = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new TeslaApiException("Wake up failed", response.StatusCode, json);
                }
            }
        }

        private async Task Login()
        {
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);

                dynamic request = new
                {
                    grant_type = "password",
                    client_id = clientId,
                    client_secret = clientSecret,
                    email = this.teslaEmail,
                    password = this.teslaPassword
                };

                using (HttpResponseMessage response = await client.PostAsync("https://owner-api.teslamotors.com/oauth/token?grant_type=password", new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json")))
                {
                    string json = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new TeslaApiException("Login failed", response.StatusCode, json);

                    await File.WriteAllTextAsync(tokenFile, json);

                    this.ParseToken(JsonConvert.DeserializeObject(json));
                }
            }
        }

        private async Task Refresh()
        {
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);

                dynamic request = new
                {
                    grant_type = "refresh_token",
                    client_id = clientId,
                    client_secret = clientSecret,
                    refresh_token = this.refreshToken
                };

                using (HttpResponseMessage response = await client.PostAsync("https://owner-api.teslamotors.com/oauth/token?grant_type=refresh_token", new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json")))
                {
                    string json = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new TeslaApiException("Token refresh failed", response.StatusCode, json);

                    await File.WriteAllTextAsync(tokenFile, json);

                    this.ParseToken(JsonConvert.DeserializeObject(json));
                }
            }
        }

        private void ParseToken(dynamic json)
        {
            this.accessToken = json.access_token.ToString();
            this.refreshToken = json.refresh_token.ToString();

            this.validUntil = DateTimeOffset
                .FromUnixTimeSeconds(long.Parse(json.created_at.ToString()))
                .AddSeconds(double.Parse(json.expires_in.ToString()))
                .AddHours(-1);
        }

        private void ClearToken()
        {
            this.accessToken = null;
            this.refreshToken = null;

            this.validUntil = DateTimeOffset.Now;

            File.Delete(tokenFile);
        }
    }
}
