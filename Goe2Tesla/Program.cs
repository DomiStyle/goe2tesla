using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Goe2Tesla
{
    class Program
    {
        private string teslaEmail = Environment.GetEnvironmentVariable("TESLA_EMAIL");
        private string teslaPassword = Environment.GetEnvironmentVariable("TESLA_PASSWORD");
        private string teslaVehicleId = Environment.GetEnvironmentVariable("TESLA_VEHICLE_ID");

        private string mqttServer = Environment.GetEnvironmentVariable("MQTT_SERVER");
        private int mqttPort = int.Parse(Environment.GetEnvironmentVariable("MQTT_PORT"));
        private string mqttUsername = Environment.GetEnvironmentVariable("MQTT_USERNAME");
        private string mqttPassword = Environment.GetEnvironmentVariable("MQTT_PASSWORD");
        private string mqttTopic = Environment.GetEnvironmentVariable("MQTT_TOPIC");

        private string accessToken = null;
        private string refreshToken = null;
        private DateTimeOffset validUntil;

        private bool previousState;
        private bool initialized = false;

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

            var factory = new MqttFactory();

            using (var mqttClient = factory.CreateMqttClient())
            {
                var options = new MqttClientOptionsBuilder()
                    .WithClientId("Goe2Tesla")
                    .WithTcpServer(mqttServer, mqttPort)
                    .WithCredentials(mqttUsername, mqttPassword)
                    .WithCleanSession()
                    .Build();

                await mqttClient.ConnectAsync(options, CancellationToken.None);

                mqttClient.UseDisconnectedHandler(async e =>
                {
                    Console.WriteLine("MQTT connection lost");

                    await Task.Delay(TimeSpan.FromSeconds(10));

                    try
                    {
                        await mqttClient.ConnectAsync(options, CancellationToken.None);
                    }
                    catch
                    {
                        Console.WriteLine("Reconnecting to MQTT server failed");
                    }
                });

                mqttClient.UseApplicationMessageReceivedHandler(async e =>
                {
                    dynamic json = JsonConvert.DeserializeObject(e.ApplicationMessage.ConvertPayloadToString());

                    bool currentState = this.ParseBool(json.alw.ToString());

                    Console.WriteLine("Charging allowed? " + currentState);

                    if (this.initialized)
                    {
                        if (currentState != previousState)
                        {
                            Console.WriteLine("State changed, waking up car");
                            await this.WakeUp();
                        }
                    }
                    else
                    {
                        this.initialized = true;
                    }

                    this.previousState = currentState;
                });

                mqttClient.UseConnectedHandler(async e =>
                {
                    Console.WriteLine("Connected to server");

                    await mqttClient.SubscribeAsync(mqttTopic);

                    Console.WriteLine("Subscribed to topic");
                });
            }
        }

        private bool ParseBool(string input)
        {
            return (input == "1");
        }

        private async Task<dynamic> WakeUp()
        {
            if(accessToken == null)
            {
                Console.WriteLine("Logging into Tesla API");
                await this.Login();
            }
            else if(DateTimeOffset.Now >= this.validUntil)
            {
                Console.WriteLine("Refreshing Tesla token");
                await this.Refresh();
            }

            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + this.accessToken);

                using (HttpResponseMessage response = await client.PostAsync(string.Format("https://owner-api.teslamotors.com/api/1/vehicles/{0}/wake_up", this.teslaVehicleId), new StringContent("")))
                {
                    return JsonConvert.DeserializeObject(await response.Content.ReadAsStringAsync());
                }
            }
        }

        private async Task Login()
        {
            using (HttpClient client = new HttpClient())
            {
                dynamic request = new
                {
                    grant_type = "password",
                    client_id = "81527cff06843c8634fdc09e8ac0abefb46ac849f38fe1e431c2ef2106796384",
                    client_secret = "c7257eb71a564034f9419ee651c7d0e5f7aa6bfbd18bafb5c5c033b093bb2fa3",
                    email = this.teslaEmail,
                    password = this.teslaPassword
                };

                using (HttpResponseMessage response = await client.PostAsync("https://owner-api.teslamotors.com/oauth/token?grant_type=password", new StringContent(JsonConvert.SerializeObject(request))))
                {
                    this.ParseTokenResponse(JsonConvert.DeserializeObject(await response.Content.ReadAsStringAsync()));
                }
            }
        }

        private async Task Refresh()
        {
            using (HttpClient client = new HttpClient())
            {
                dynamic request = new
                {
                    grant_type = "refresh_token",
                    client_id = "81527cff06843c8634fdc09e8ac0abefb46ac849f38fe1e431c2ef2106796384",
                    client_secret = "c7257eb71a564034f9419ee651c7d0e5f7aa6bfbd18bafb5c5c033b093bb2fa3",
                    refresh_token = this.refreshToken
                };

                using (HttpResponseMessage response = await client.PostAsync("https://owner-api.teslamotors.com/oauth/token?grant_type=refresh_token", new StringContent(JsonConvert.SerializeObject(request))))
                {
                    this.ParseTokenResponse(JsonConvert.DeserializeObject(await response.Content.ReadAsStringAsync()));
                }
            }
        }

        private void ParseTokenResponse(dynamic json)
        {
            this.accessToken = json.access_token;
            this.refreshToken = json.refresh_token;
            this.validUntil = DateTimeOffset.FromUnixTimeSeconds(long.Parse(json.created_at.ToString()));

            this.validUntil.AddSeconds(json.expires_in);
        }
    }
}
