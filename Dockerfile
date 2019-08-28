FROM mcr.microsoft.com/dotnet/core/runtime:2.1

ENV MQTT_PORT=1883

COPY app /app

WORKDIR /app
ENTRYPOINT ["dotnet", "goe2tesla.dll"]