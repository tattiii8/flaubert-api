variable "image_tag" {
  type        = string
  description = "Docker image tag"
  default     = "latest"
}

variable "ecr_registry" {
  type        = string
  description = "ECR registry URL"
}

job "flaubert-auth-api" {
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
        image = "${var.ecr_registry}/flaubert-auth-api:${var.image_tag}"
        ports = ["http"]
        force_pull = true
      }

      env {
        ASPNETCORE_URLS = "http://+:8080"
        ASPNETCORE_ENVIRONMENT = "Production"
      }

      resources {
        cpu    = 500
        memory = 512
      }

      service {
        name = "flaubert-auth-api"
        port = "http"
        
        tags = [
          "api",
          "auth",
          "dotnet9"
        ]
        
        check {
          type     = "http"
          path     = "/health"
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