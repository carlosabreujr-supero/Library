FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY LibraryLoans.sln ./
COPY src/LibraryLoans.Api/LibraryLoans.Api.csproj src/LibraryLoans.Api/
COPY src/LibraryLoans.Application/LibraryLoans.Application.csproj src/LibraryLoans.Application/
COPY src/LibraryLoans.Domain/LibraryLoans.Domain.csproj src/LibraryLoans.Domain/
COPY src/LibraryLoans.Infrastructure/LibraryLoans.Infrastructure.csproj src/LibraryLoans.Infrastructure/
RUN dotnet restore src/LibraryLoans.Api/LibraryLoans.Api.csproj

COPY src/ src/
RUN dotnet publish src/LibraryLoans.Api/LibraryLoans.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "LibraryLoans.Api.dll"]
