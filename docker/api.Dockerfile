FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Directory.Build.props", "./"]
COPY ["Directory.Product.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["src/Dotnet10Template.Api/Dotnet10Template.Api.csproj", "src/Dotnet10Template.Api/"]
COPY ["src/Dotnet10Template.Application/Dotnet10Template.Application.csproj", "src/Dotnet10Template.Application/"]
COPY ["src/Dotnet10Template.Domain/Dotnet10Template.Domain.csproj", "src/Dotnet10Template.Domain/"]
COPY ["src/Dotnet10Template.Infrastructure/Dotnet10Template.Infrastructure.csproj", "src/Dotnet10Template.Infrastructure/"]

RUN dotnet restore "src/Dotnet10Template.Api/Dotnet10Template.Api.csproj"

COPY . .

WORKDIR "/src/src/Dotnet10Template.Api"

RUN dotnet publish "Dotnet10Template.Api.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false
RUN dotnet msbuild "Dotnet10Template.Api.csproj" \
    -nologo \
    -getProperty:ApiExecutableName > /app/publish/api-executable-name

FROM base AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["sh", "-c", "dotnet \"$(cat /app/api-executable-name).dll\""]
