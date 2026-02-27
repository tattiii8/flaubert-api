job "flaubert-api" {
  datacenters = ["dc1"]
  type        = "service"

  group "api" {
    count = 1

    network {
      port "http" {
        static = 8000
      }
    }

    task "fastapi" {
      driver = "docker"

      config {
        image = "__ECR_IMAGE__"
        ports = ["http"]
      }

      resources {
        cpu    = 256
        memory = 256
      }

      service {
        name = "flaubert-api"
        port = "http"

        check {
          type     = "http"
          path     = "/health"
          interval = "10s"
          timeout  = "2s"
        }
      }
    }
  }
}