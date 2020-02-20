FROM mcr.microsoft.com/dotnet/core/runtime:3.1

ENV MQTT_PORT=1883 \
    TOKEN_FILE=/app/token.json \
    WAKEUP_RETRIES=5 \
    PRE_WAKEUP_ENABLE=false \
    PRE_WAKEUP_TIME=21:00 \
    NOTIFY_ENABLE=false \
    NOTIFY_TELEGRAM_TOKEN=0 \
    NOTIFY_TELEGRAM_CHAT=0

COPY app /app

WORKDIR /app
ENTRYPOINT ["dotnet", "goe2tesla.dll"]
