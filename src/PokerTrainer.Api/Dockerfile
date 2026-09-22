# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Build.props", "./"]
COPY ["Directory.Product.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["src/PokerTrainer.Api/PokerTrainer.Api.csproj", "src/PokerTrainer.Api/"]
COPY ["src/PokerTrainer.Application/PokerTrainer.Application.csproj", "src/PokerTrainer.Application/"]
COPY ["src/PokerTrainer.Domain/PokerTrainer.Domain.csproj", "src/PokerTrainer.Domain/"]
COPY ["src/PokerTrainer.Infrastructure/PokerTrainer.Infrastructure.csproj", "src/PokerTrainer.Infrastructure/"]
RUN dotnet restore "./src/PokerTrainer.Api/PokerTrainer.Api.csproj"
COPY . .
WORKDIR "/src/src/PokerTrainer.Api"
RUN dotnet build "./PokerTrainer.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./PokerTrainer.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
RUN dotnet msbuild "./PokerTrainer.Api.csproj" -nologo -getProperty:ApiExecutableName > /app/publish/api-executable-name

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["sh", "-c", "dotnet \"$(cat /app/api-executable-name).dll\""]
