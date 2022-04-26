dotnet test /p:CollectCoverage=true /p:CoverletOutput=Coverage\ /p:CoverletOutputFormat=cobertura 
reportgenerator -reports:"super-rpc.tests\Coverage\coverage.cobertura.xml" -targetdir:"coveragereport" -reporttypes:Html
