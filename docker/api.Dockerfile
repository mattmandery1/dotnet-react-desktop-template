FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Directory.Build.props", "./"]
COPY ["Directory.Product.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["src/PokerTrainer.Api/PokerTrainer.Api.csproj", "src/PokerTrainer.Api/"]
COPY ["src/PokerTrainer.Application/PokerTrainer.Application.csproj", "src/PokerTrainer.Application/"]
COPY ["src/PokerTrainer.Domain/PokerTrainer.Domain.csproj", "src/PokerTrainer.Domain/"]
COPY ["src/PokerTrainer.Infrastructure/PokerTrainer.Infrastructure.csproj", "src/PokerTrainer.Infrastructure/"]

RUN dotnet restore "src/PokerTrainer.Api/PokerTrainer.Api.csproj"

COPY . .

WORKDIR "/src/src/PokerTrainer.Api"

RUN dotnet publish "PokerTrainer.Api.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false
RUN dotnet msbuild "PokerTrainer.Api.csproj" \
    -nologo \
    -getProperty:ApiExecutableName > /app/publish/api-executable-name

FROM base AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["sh", "-c", "dotnet \"$(cat /app/api-executable-name).dll\""]
