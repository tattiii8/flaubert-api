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

      template {
        data = <<EOF
{{ with nomadVar "nomad/jobs/flaubert-api" }}
DB_HOST={{ .FLAUBERT_DB_HOST }}
DB_PORT=3306
DB_USER={{ .FLAUBERT_DB_USER }}
DB_PASSWORD={{ .FLAUBERT_DB_PASSWORD }}
DB_NAME={{ .FLAUBERT_DB_NAME }}
{{ end }}
EOF
        destination = "secrets/.env"
        env         = true
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
          path     = "/api/v1/health"
          interval = "10s"
          timeout  = "2s"
        }
      }
    }
  }
}