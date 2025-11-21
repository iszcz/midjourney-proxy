FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY . ./
EXPOSE 8186
ENTRYPOINT ["dotnet", "Midjourney.YarpProxy.dll"]