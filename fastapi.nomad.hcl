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

      env {
        DB_HOST     = "192.168.8.23"
        DB_PORT     = "3306"
        DB_USER     = "proxysql"
        DB_PASSWORD = "040629602t"
        DB_NAME     = "redmine"
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