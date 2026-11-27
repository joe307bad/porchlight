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
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
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
      }, {
      # The CDN's origin secret (below): deploy.sh hands it to Caddy.
      Effect   = "Allow"
      Action   = ["secretsmanager:GetSecretValue"]
      Resource = aws_secretsmanager_secret.cdn_origin.arn
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
  size      = 10
  type      = "gp3"
  encrypted = true

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
# The game build's CDN: cdn.<domain>, CloudFront in front of this box
# =============================================================================
# In November 2026 a crawler rendered made-up paths, each pulling the ~24 MB
# WebGL build, ~750 GB a day of EC2 egress at $0.09/GB (#26). The page now
# loads the build from here, and only from here
# (unity/Assets/WebGLTemplates/Porchlight/index.html). CloudFront keeps it at the
# edge for a day and each deploy invalidates it, so the box sends a build about
# once per edge per deploy, and CloudFront's first 1 TB a month is free. Only /Build/* is served; anything
# else on cdn.<domain> is a 404 at the edge, so crawlers can't reach the box
# through it. And the box serves the build only to CloudFront (the origin
# secret below), so it can't be pulled from the box directly either.

variable "dns_zone" {
  description = "The Route53 zone the CDN's records go in (it predates this stack; only these records are managed here)"
  default     = "joebad.com"
}

locals {
  cdn_domain = "cdn.${var.domain}"
}

data "aws_route53_zone" "main" {
  name = var.dns_zone
}

# CloudFront takes certificates from us-east-1 only, which is this stack's region.
resource "aws_acm_certificate" "cdn" {
  domain_name       = local.cdn_domain
  validation_method = "DNS"
  tags              = { Name = "porchlight-cdn" }

  lifecycle {
    create_before_destroy = true
  }
}

resource "aws_route53_record" "cdn_validation" {
  for_each = {
    for o in aws_acm_certificate.cdn.domain_validation_options : o.domain_name => {
      name   = o.resource_record_name
      type   = o.resource_record_type
      record = o.resource_record_value
    }
  }

  zone_id         = data.aws_route53_zone.main.zone_id
  name            = each.value.name
  type            = each.value.type
  records         = [each.value.record]
  ttl             = 300
  allow_overwrite = true
}

resource "aws_acm_certificate_validation" "cdn" {
  certificate_arn         = aws_acm_certificate.cdn.arn
  validation_record_fqdns = [for r in aws_route53_record.cdn_validation : r.fqdn]
}

# Kept a day at the edge whatever the box says (Caddy sends no-cache, which
# with a minimum TTL of 0 CloudFront takes as "don't cache", and every request
# went to the box). Each deploy invalidates /Build/* (deploy.yml), since the
# build's file names don't change. Browsers still get the box's no-cache, so
# they revalidate with the edge (a 304) instead of with the box. Nothing from
# the viewer is in the key or sent on: the box gets its own Host, so Caddy
# knows the site.
resource "aws_cloudfront_cache_policy" "build" {
  name        = "porchlight-build"
  min_ttl     = 86400
  default_ttl = 86400
  max_ttl     = 31536000

  parameters_in_cache_key_and_forwarded_to_origin {
    enable_accept_encoding_gzip   = false
    enable_accept_encoding_brotli = false

    cookies_config {
      cookie_behavior = "none"
    }
    headers_config {
      header_behavior = "none"
    }
    query_strings_config {
      query_string_behavior = "none"
    }
  }
}

# Only CloudFront may fetch the build from the box: it sends this secret in a
# header on every request to the box, and Caddy refuses /Build/* without it,
# so nobody can skip the CDN and download the build at EC2 prices. Kept in its
# own secret, which deploy.sh reads and hands to Caddy (CDN_ORIGIN_SECRET).
resource "random_password" "cdn_origin" {
  length  = 48
  special = false
}

resource "aws_secretsmanager_secret" "cdn_origin" {
  name                    = "porchlight/cdn-origin"
  recovery_window_in_days = 0
  tags                    = { Name = "porchlight-cdn-origin" }
}

resource "aws_secretsmanager_secret_version" "cdn_origin" {
  secret_id     = aws_secretsmanager_secret.cdn_origin.id
  secret_string = random_password.cdn_origin.result
}

# Everything that isn't the build: a 404 at the edge, never the box.
resource "aws_cloudfront_function" "not_found" {
  name    = "porchlight-cdn-not-found"
  runtime = "cloudfront-js-2.0"
  publish = true
  code    = <<-JS
    function handler(event) {
      return { statusCode: 404, statusDescription: 'Not Found' };
    }
  JS
}

resource "aws_cloudfront_distribution" "cdn" {
  enabled         = true
  comment         = "Porchlight: the game's WebGL build (${local.cdn_domain})"
  aliases         = [local.cdn_domain]
  price_class     = "PriceClass_100"
  http_version    = "http2and3"
  is_ipv6_enabled = true

  origin {
    origin_id   = "box"
    domain_name = var.domain

    custom_header {
      name  = "X-Porchlight-Origin"
      value = random_password.cdn_origin.result
    }

    custom_origin_config {
      http_port              = 80
      https_port             = 443
      origin_protocol_policy = "https-only"
      origin_ssl_protocols   = ["TLSv1.2"]
    }
  }

  default_cache_behavior {
    target_origin_id       = "box"
    viewer_protocol_policy = "redirect-to-https"
    allowed_methods        = ["GET", "HEAD"]
    cached_methods         = ["GET", "HEAD"]
    cache_policy_id        = aws_cloudfront_cache_policy.build.id

    function_association {
      event_type   = "viewer-request"
      function_arn = aws_cloudfront_function.not_found.arn
    }
  }

  ordered_cache_behavior {
    path_pattern           = "/Build/*"
    target_origin_id       = "box"
    viewer_protocol_policy = "redirect-to-https"
    # OPTIONS: the CORS preflight Unity's loader causes (Caddy answers it).
    allowed_methods = ["GET", "HEAD", "OPTIONS"]
    cached_methods  = ["GET", "HEAD", "OPTIONS"]
    cache_policy_id = aws_cloudfront_cache_policy.build.id
    # The .unityweb files are already gzipped (and say so); this compresses the loader.
    compress = true
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  viewer_certificate {
    acm_certificate_arn      = aws_acm_certificate_validation.cdn.certificate_arn
    ssl_support_method       = "sni-only"
    minimum_protocol_version = "TLSv1.2_2021"
  }

  tags = { Name = "porchlight-cdn" }
}

resource "aws_route53_record" "cdn" {
  for_each = toset(["A", "AAAA"])

  zone_id = data.aws_route53_zone.main.zone_id
  name    = local.cdn_domain
  type    = each.key

  alias {
    name                   = aws_cloudfront_distribution.cdn.domain_name
    zone_id                = aws_cloudfront_distribution.cdn.hosted_zone_id
    evaluate_target_health = false
  }
}

# =============================================================================
# Alarms: egress that looks like that crawler again, within the hour
# =============================================================================
# The billing alert (an AWS Budget on DataTransfer-Out-Bytes, made by hand)
# runs a day behind; these go off within the hour. The topic's email
# subscriber is added by hand, not here, so the address stays out of this
# public repo:
#   aws sns subscribe --topic-arn <alerts_topic_arn> --protocol email --notification-endpoint <address>
# (and click the confirmation link AWS emails).

resource "aws_sns_topic" "alerts" {
  name = "porchlight-alerts"
}

# Three or four players use about 1 GB an hour (the traffic feed is most of
# it); the crawler was ~31 GB an hour.
resource "aws_cloudwatch_metric_alarm" "egress" {
  alarm_name          = "porchlight-egress"
  alarm_description   = "The box sent over 5 GB in an hour (normal play is ~1 GB). Check Caddy's logs for a crawler: #26."
  namespace           = "AWS/EC2"
  metric_name         = "NetworkOut"
  dimensions          = { InstanceId = aws_instance.main.id }
  statistic           = "Sum"
  period              = 3600
  evaluation_periods  = 1
  threshold           = 5000000000
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
  alarm_actions       = [aws_sns_topic.alerts.arn]
  ok_actions          = [aws_sns_topic.alerts.arn]
}

# CloudFront's free tier is 1 TB a month (~1.4 GB an hour); past that it's
# $0.085/GB. 10 GB in an hour is several thousand fresh loads of the game.
resource "aws_cloudwatch_metric_alarm" "cdn_egress" {
  alarm_name          = "porchlight-cdn-egress"
  alarm_description   = "The CDN sent over 10 GB in an hour. Check its logs or the CloudFront console for who: #26."
  namespace           = "AWS/CloudFront"
  metric_name         = "BytesDownloaded"
  dimensions          = { DistributionId = aws_cloudfront_distribution.cdn.id, Region = "Global" }
  statistic           = "Sum"
  period              = 3600
  evaluation_periods  = 1
  threshold           = 10000000000
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
  alarm_actions       = [aws_sns_topic.alerts.arn]
  ok_actions          = [aws_sns_topic.alerts.arn]
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

output "cdn_domain" {
  value = local.cdn_domain
}

output "alerts_topic_arn" {
  description = "Subscribe an email to it by hand (see the Alarms section)"
  value       = aws_sns_topic.alerts.arn
}

output "cdn_distribution_id" {
  description = "The deploy invalidates /Build/* here"
  value       = aws_cloudfront_distribution.cdn.id
}

output "cdn_origin_secret_id" {
  description = "deploy.sh reads the CDN's origin secret from here"
  value       = aws_secretsmanager_secret.cdn_origin.id
}
