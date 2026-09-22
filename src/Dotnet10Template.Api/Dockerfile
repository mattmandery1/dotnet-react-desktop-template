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
COPY ["src/Dotnet10Template.Api/Dotnet10Template.Api.csproj", "src/Dotnet10Template.Api/"]
COPY ["src/Dotnet10Template.Application/Dotnet10Template.Application.csproj", "src/Dotnet10Template.Application/"]
COPY ["src/Dotnet10Template.Domain/Dotnet10Template.Domain.csproj", "src/Dotnet10Template.Domain/"]
COPY ["src/Dotnet10Template.Infrastructure/Dotnet10Template.Infrastructure.csproj", "src/Dotnet10Template.Infrastructure/"]
RUN dotnet restore "./src/Dotnet10Template.Api/Dotnet10Template.Api.csproj"
COPY . .
WORKDIR "/src/src/Dotnet10Template.Api"
RUN dotnet build "./Dotnet10Template.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Dotnet10Template.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
RUN dotnet msbuild "./Dotnet10Template.Api.csproj" -nologo -getProperty:ApiExecutableName > /app/publish/api-executable-name

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["sh", "-c", "dotnet \"$(cat /app/api-executable-name).dll\""]
