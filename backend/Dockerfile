# Build and publish the API with the .NET 10 SDK.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY MyLittleLibrary.Api/MyLittleLibrary.Api.csproj MyLittleLibrary.Api/
RUN dotnet restore MyLittleLibrary.Api/MyLittleLibrary.Api.csproj
COPY . .
RUN dotnet publish MyLittleLibrary.Api/MyLittleLibrary.Api.csproj -c Release -o /app/publish --no-restore

# Run on the ASP.NET Core 10 runtime.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MyLittleLibrary.Api.dll"]
