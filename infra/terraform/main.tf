# =============================================================================
# Porchlight on AWS: one small EC2 box running Docker Compose
# =============================================================================
# The same shape as topspin's infra/terraform, shrunk to the least that runs
# Keycloak: a t4g.micro (2 vCPU burstable, 1 GB, arm64) with swap, a small
# persistent data volume for Keycloak's database and Caddy's certificates, and
# an elastic IP for the two DNS names.
#
# State lives in S3 (bucket created by the deploy workflow; see its
# "Terraform state bucket" step). Applied by .github/workflows/deploy.yml.

terraform {
  required_version = ">= 1.10"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
  }

  # Partial: bucket, key and region are passed to `terraform init` by the
  # workflow (the bucket name includes the account id). S3-native locking, so
  # there's no DynamoDB table to pay for.
  backend "s3" {
    use_lockfile = true
    encrypt      = true
  }
}

# =============================================================================
# Variables
# =============================================================================

variable "aws_region" {
  default = "us-east-1"
}

variable "domain" {
  description = "Where the app is served; Keycloak goes on auth.<domain>"
  default     = "porchlight.joebad.com"
}

variable "instance_type" {
  # 2 vCPUs and 2 GB. Keycloak (heap capped at 320 MB), Postgres, Caddy and the
  # auth + game server come to about 0.9 GB, which on the 1 GB t4g.micro this
  # started on meant living partly in swap; 2 GB holds it all in memory with
  # room to spare. The 2 GB swap file (user-data) stays for Keycloak's start-up
  # spike. Changing this stops and starts the box in place (same disks, same
  # IP), a minute or two of downtime. Nothing is built from the app on the
  # box; CI builds it.
  default = "t4g.small"
}

variable "ssh_public_key" {
  description = "SSH public key for EC2 access (the deploy workflow's key)"
  type        = string
}

variable "allowed_ssh_cidr" {
  default = "0.0.0.0/0"
}

# =============================================================================
# Provider
# =============================================================================

provider "aws" {
  region = var.aws_region

  default_tags {
    tags = {
      Project   = "porchlight"
      ManagedBy = "terraform"
    }
  }
}

# Amazon Linux 2023, arm64, the full image (not -minimal).
data "aws_ami" "amazon_linux" {
  most_recent = true
  owners      = ["amazon"]

  filter {
    name   = "name"
    values = ["al2023-ami-2023.*-kernel-*-arm64"]
  }

  filter {
    name   = "architecture"
    values = ["arm64"]
  }
}

# =============================================================================
# Networking
# =============================================================================

resource "aws_vpc" "main" {
  cidr_block           = "10.1.0.0/16"
  enable_dns_hostnames = true
  enable_dns_support   = true
  tags                 = { Name = "porchlight-vpc" }
}

resource "aws_subnet" "public" {
  vpc_id                  = aws_vpc.main.id
  cidr_block              = "10.1.1.0/24"
  availability_zone       = "${var.aws_region}a"
  map_public_ip_on_launch = true
  tags                    = { Name = "porchlight-subnet" }
}

resource "aws_internet_gateway" "main" {
  vpc_id = aws_vpc.main.id
  tags   = { Name = "porchlight-igw" }
}

resource "aws_route_table" "public" {
  vpc_id = aws_vpc.main.id

  route {
    cidr_block = "0.0.0.0/0"
    gateway_id = aws_internet_gateway.main.id
  }

  tags = { Name = "porchlight-rt" }
}

resource "aws_route_table_association" "public" {
  subnet_id      = aws_subnet.public.id
  route_table_id = aws_route_table.public.id
}

resource "aws_security_group" "main" {
  name        = "porchlight-sg"
  description = "Porchlight: SSH, HTTP, HTTPS"
  vpc_id      = aws_vpc.main.id

  ingress {
    description = "SSH"
    from_port   = 22
    to_port     = 22
    protocol    = "tcp"
    cidr_blocks = [var.allowed_ssh_cidr]
  }

  ingress {
    description = "HTTP (ACME challenges, redirect to HTTPS)"
    from_port   = 80
    to_port     = 80
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  ingress {
    description = "HTTPS"
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  ingress {
    description = "HTTP/3"
    from_port   = 443
    to_port     = 443
    protocol    = "udp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = { Name = "porchlight-sg" }
}

# =============================================================================
# Secrets Manager
# =============================================================================
# One secret, read (and on first deploy, completed) by infra/docker/deploy.sh.
# The workflow writes the Google OAuth client into it; deploy.sh generates the
# rest. `ignore_changes` so Terraform never clobbers what they wrote.

resource "aws_secretsmanager_secret" "main" {
  name                    = "porchlight/prod"
  recovery_window_in_days = 0
  tags                    = { Name = "porchlight-secrets" }
}

resource "aws_secretsmanager_secret_version" "main" {
  secret_id = aws_secretsmanager_secret.main.id

  secret_string = jsonencode({
    GOOGLE_CLIENT_ID        = ""
    GOOGLE_CLIENT_SECRET    = ""
    POSTGRES_PASSWORD       = ""
    KEYCLOAK_ADMIN_PASSWORD = ""
    KEYCLOAK_CLIENT_SECRET  = ""
    SESSION_SECRET          = ""
  })

  lifecycle {
    ignore_changes = [secret_string]
  }
}

# =============================================================================
# IAM: the box reads its secret, and writes back the ones it generates
# =============================================================================

resource "aws_iam_role" "ec2" {
  name = "porchlight-ec2-role"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Action    = "sts:AssumeRole"
      Effect    = "Allow"
      Principal = { Service = "ec2.amazonaws.com" }
    }]
  })
}

resource "aws_iam_role_policy" "ec2" {
  name = "porchlight-ec2-policy"
  role = aws_iam_role.ec2.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect   = "Allow"
      Action   = ["secretsmanager:GetSecretValue", "secretsmanager:PutSecretValue"]
      Resource = aws_secretsmanager_secret.main.arn
    }]
  })
}

resource "aws_iam_instance_profile" "ec2" {
  name = "porchlight-ec2-profile"
  role = aws_iam_role.ec2.name
}

resource "aws_key_pair" "main" {
  key_name   = "porchlight-key"
  public_key = var.ssh_public_key
}

# =============================================================================
# Persistent data (survives instance replacement): Postgres, Caddy's certs
# =============================================================================

resource "aws_ebs_volume" "data" {
  availability_zone = "${var.aws_region}a"
  # Grown in place (deploy.sh grows the filesystem to match). EBS allows one
  # change per volume every six hours.
  size = 10
  type              = "gp3"
  encrypted         = true

  tags = {
    Name     = "porchlight-data"
    Snapshot = "true"
  }

  lifecycle {
    prevent_destroy = true
  }
}

resource "aws_iam_role" "dlm" {
  name = "porchlight-dlm-role"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Action    = "sts:AssumeRole"
      Effect    = "Allow"
      Principal = { Service = "dlm.amazonaws.com" }
    }]
  })
}

resource "aws_iam_role_policy" "dlm" {
  name = "porchlight-dlm-policy"
  role = aws_iam_role.dlm.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = [
          "ec2:CreateSnapshot",
          "ec2:CreateSnapshots",
          "ec2:DeleteSnapshot",
          "ec2:DescribeInstances",
          "ec2:DescribeVolumes",
          "ec2:DescribeSnapshots",
        ]
        Resource = "*"
      },
      {
        Effect   = "Allow"
        Action   = ["ec2:CreateTags"]
        Resource = "arn:aws:ec2:*::snapshot/*"
      },
    ]
  })
}

# Daily snapshots of the data volume, a week kept. Users live here.
resource "aws_dlm_lifecycle_policy" "snapshots" {
  description        = "Daily snapshots of the porchlight data volume"
  execution_role_arn = aws_iam_role.dlm.arn
  state              = "ENABLED"

  policy_details {
    resource_types = ["VOLUME"]

    schedule {
      name = "Daily snapshots with 7-day retention"

      create_rule {
        interval      = 24
        interval_unit = "HOURS"
        times         = ["03:00"]
      }

      retain_rule {
        count = 7
      }

      copy_tags = true
    }

    target_tags = {
      Snapshot = "true"
    }
  }

  tags = { Name = "porchlight-snapshots" }
}

# =============================================================================
# The box
# =============================================================================

resource "aws_instance" "main" {
  ami                    = data.aws_ami.amazon_linux.id
  instance_type          = var.instance_type
  key_name               = aws_key_pair.main.key_name
  vpc_security_group_ids = [aws_security_group.main.id]
  subnet_id              = aws_subnet.public.id
  iam_instance_profile   = aws_iam_instance_profile.ec2.name

  # Unlimited, the t4g default. In "standard" mode a new t4g starts with no
  # CPU credits and is held to its 10% baseline, which turned Keycloak's first
  # start into many minutes. An idle site stays under the baseline and costs
  # nothing extra; only sustained use above it is billed.
  credit_specification {
    cpu_credits = "unlimited"
  }

  # Grown in place like the data volume (deploy.sh grows the partition and
  # filesystem): images and their layers are what fill it.
  root_block_device {
    volume_size = 20
    volume_type = "gp3"
    encrypted   = true
  }

  metadata_options {
    http_tokens = "required" # IMDSv2 only
  }

  # Replacing the box for a newer AMI or edited bootstrap is a deliberate act
  # (terraform taint / -replace), not a side effect of a deploy. The root
  # volume's size is managed (it grows in place); its other settings aren't.
  lifecycle {
    ignore_changes = [ami, user_data, root_block_device[0].encrypted, root_block_device[0].volume_type]
  }

  user_data = base64encode(templatefile("${path.module}/scripts/user-data.sh", {
    data_volume_id = aws_ebs_volume.data.id
  }))

  tags = { Name = "porchlight" }

  depends_on = [aws_secretsmanager_secret_version.main]
}

resource "aws_volume_attachment" "data" {
  device_name = "/dev/xvdf"
  volume_id   = aws_ebs_volume.data.id
  instance_id = aws_instance.main.id
}

resource "aws_eip" "main" {
  domain = "vpc"
  tags   = { Name = "porchlight-eip" }
}

resource "aws_eip_association" "main" {
  instance_id   = aws_instance.main.id
  allocation_id = aws_eip.main.id
}

# =============================================================================
# Outputs
# =============================================================================
# DNS is not managed here, as in topspin: the zone predates this stack and
# holds records it doesn't own. The workflow upserts the two A records with
# the AWS CLI when it can, best-effort; otherwise create them by hand.

output "elastic_ip" {
  value = aws_eip.main.public_ip
}

output "instance_id" {
  value = aws_instance.main.id
}

output "secret_id" {
  value = aws_secretsmanager_secret.main.id
}

output "ssh_command" {
  value = "ssh ec2-user@${aws_eip.main.public_ip}"
}

output "dns_instructions" {
  value = join("\n", [
    "A ${var.domain} -> ${aws_eip.main.public_ip}",
    "A auth.${var.domain} -> ${aws_eip.main.public_ip}",
  ])
}

output "google_redirect_uri" {
  description = "Add to the Google OAuth client's authorized redirect URIs"
  value       = "https://auth.${var.domain}/realms/porchlight/broker/google/endpoint"
}
