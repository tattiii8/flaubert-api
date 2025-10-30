variable "image_tag" {
  type        = string
  description = "Docker image tag"
  default     = "latest"
}

variable "ecr_registry" {
  type        = string
  description = "ECR registry URL"
}

job "flaubert-data-api" {
  datacenters = ["dc1"]
  type        = "service"

  group "api" {
    count = 1

    network {
      port "http" {
        to = 8080
      }
    }

    task "api" {
      driver = "docker"

      config {
        image = "${var.ecr_registry}/flaubert-data-api:${var.image_tag}"
        ports = ["http"]
        force_pull = true
      }

      env {
        ASPNETCORE_URLS = "http://+:8080"
        ASPNETCORE_ENVIRONMENT = "Production"
        JWT_SECRET   = "wOsiYsPhzZg0wqS6PdQMkI6ZYB0gY/BU12JBK/Oqu8c=%"
      }

      resources {
        cpu    = 500
        memory = 512
      }

      service {
        name = "flaubert-data-api"
        port = "http"
        
        tags = [
          "api",
          "data",
          "dotnet9"
        ]
        
        check {
          type     = "http"
          path     = "/data/api/v1/health"
          interval = "10s"
          timeout  = "2s"
        }
      }
    }

    update {
      max_parallel     = 1
      min_healthy_time = "10s"
      healthy_deadline = "3m"
      auto_revert      = true
    }
  }
}