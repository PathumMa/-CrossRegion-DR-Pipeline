# Cross-Region Disaster Recovery Pipeline for Containerised Applications

An automated, cross-region disaster recovery (DR) pipeline for containerised applications, built with Azure DevOps and extended with a multi-cloud research strand. This repository contains the sample ASP.NET Web API application and the CI/CD pipeline that builds, tests and deploys it.

## Project Overview

Most containerised applications are deployed to a single Azure region and rely on local high-availability features to absorb failures. This project automates recovery at the regional level instead: if the primary region becomes unavailable, the pipeline and supporting infrastructure are designed to fail the application over to a secondary region with minimal manual intervention.

Azure is the primary platform for the working implementation. As a secondary research strand, alternative cloud providers (AWS, GCP, Huawei Cloud, Oracle Cloud Infrastructure) are being evaluated for their suitability as a DR target, with Azure DevOps intended to remain the single orchestration layer regardless of where the secondary environment is hosted.

## Architecture

- **Primary region:** Southeast Asia
- **Secondary region:** France Central
- **Compute:** Azure Container Apps
- **Registry:** Azure Container Registry (ACR)
- **CI/CD:** Azure DevOps Pipelines
- **Planned:** geo-replicated data layer, Azure Front Door / Traffic Manager for global routing, Azure Monitor / Application Insights for failure detection

A full architecture diagram is included in the project's interim report and viva presentation.

## Current Status

This project is under active development as part of a university final-year module (FC6P01ES). At present:

- [x] Azure subscription and Azure DevOps project provisioned
- [x] Sample ASP.NET Web API application created
- [x] CI pipeline builds the application and pushes the container image to ACR
- [x] Single-region deployment to Azure Container Apps (Southeast Asia) verified
- [ ] Secondary region deployment (France Central)
- [ ] Data layer with geo-replication
- [ ] Global traffic routing and automated failover
- [ ] Monitoring and automated DR runbook
- [ ] Multi-cloud provider evaluation

## Repository Structure

```
/
├── src/                 # ASP.NET Web API application source
├── pipelines/           # Azure DevOps pipeline YAML definitions
├── docs/                # Architecture diagrams and supporting documentation
└── README.md
```

## Getting Started

### Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) (version matching the project's target framework)
- Docker
- An Azure subscription with access to Azure Container Registry and Azure Container Apps
- Azure CLI

### Running Locally

```bash
git clone <repository-url>
cd <repository-folder>
dotnet restore
dotnet run
```

The application will be available at `https://localhost:<port>`.

### Running in a Container

```bash
docker build -t crossregion-dr-app .
docker run -p 8080:80 crossregion-dr-app
```

## CI/CD Pipeline

The Azure DevOps pipeline defined in `pipelines/` automates the following on each commit to the main branch:

1. Restores and builds the .NET application
2. Builds a Docker image
3. Pushes the image to Azure Container Registry
4. Deploys the image to Azure Container Apps in the primary region

## Author

Final-year project, module FC6P01ES, supervised by Mr. Sudesh Bandara.

## License

This repository is produced for academic purposes as part of a university module and is not licensed for external distribution.