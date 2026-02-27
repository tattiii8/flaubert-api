job "fastapi-sample" {
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
        image = "ghcr.io/__GITHUB_OWNER__/__REPO_NAME__:__IMAGE_TAG__"
        ports = ["http"]
      }

      resources {
        cpu    = 256
        memory = 256
      }

      service {
        name = "fastapi-sample"
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