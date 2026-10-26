# Builds a ready-to-run plugin host image. HOST picks the host project:
#   docker build -t fleans-camunda-external-task .                                   (default)
#   docker build --build-arg HOST=Fleans.RestCaller.Host -t fleans-rest-caller .
ARG HOST=Fleans.CamundaExternalTask.Host

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG HOST
WORKDIR /src
COPY . .
RUN dotnet publish src/$HOST/$HOST.csproj -c Release -o /app \
    && ln -s /app/$HOST.dll /app/host.dll

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "host.dll"]
