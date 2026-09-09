/*M!999999\- enable the sandbox mode */ 
-- MariaDB dump 10.19  Distrib 10.6.23-MariaDB, for debian-linux-gnu (x86_64)
--
-- Host: localhost    Database: server
-- ------------------------------------------------------
-- Server version	10.6.23-MariaDB-0ubuntu0.22.04.1

/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!40101 SET NAMES utf8mb4 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;

--
-- Table structure for table `ams_data`
--

DROP TABLE IF EXISTS `ams_data`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `ams_data` (
  `statistic_dir` varchar(10) NOT NULL DEFAULT '-',
  `mailing_id` varchar(10) NOT NULL DEFAULT '-',
  `profile_id` varchar(10) NOT NULL DEFAULT '-',
  `proxy_list_id` varchar(10) NOT NULL DEFAULT '-',
  `sender_account_id` varchar(10) NOT NULL DEFAULT '-',
  `mail_list_id` varchar(10) NOT NULL DEFAULT '-',
  `exclude_list_id` varchar(10) NOT NULL DEFAULT '-',
  `message_id` varchar(10) NOT NULL DEFAULT '-',
  `last_send` date NOT NULL DEFAULT '0000-00-00'
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3 COLLATE=utf8mb3_bin;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `domain_data`
--

DROP TABLE IF EXISTS `domain_data`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `domain_data` (
  `mx_name` varchar(30) NOT NULL,
  `subdomain_name` varchar(30) NOT NULL,
  `dkim_selector` varchar(10) NOT NULL,
  `dkim_public_key` text NOT NULL,
  `dkim_private_key` text NOT NULL,
  `fbl_mailbox` varchar(15) NOT NULL,
  `refresh_token` varchar(50) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3 COLLATE=utf8mb3_bin;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `emails_list`
--

DROP TABLE IF EXISTS `emails_list`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `emails_list` (
  `id` int(11) NOT NULL AUTO_INCREMENT,
  `email` varchar(45) NOT NULL,
  `email_alias` varchar(45) NOT NULL,
  `is_progrev` varchar(1) NOT NULL,
  `last_send` date NOT NULL,
  `last_open` date NOT NULL,
  `last_click` date NOT NULL,
  `user_ip` varchar(16) NOT NULL,
  `messages_counter` int(11) NOT NULL,
  `exclude` varchar(1) NOT NULL DEFAULT '-',
  PRIMARY KEY (`id`),
  UNIQUE KEY `id_UNIQUE` (`id`),
  UNIQUE KEY `email_UNIQUE` (`email`),
  UNIQUE KEY `email_alias_UNIQUE` (`email_alias`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3 COLLATE=utf8mb3_bin;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `server_data`
--

DROP TABLE IF EXISTS `server_data`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `server_data` (
  `server` varchar(15) NOT NULL,
  `server_password` varchar(30) NOT NULL,
  `access_password` varchar(30) NOT NULL,
  `proxy_port` varchar(5) NOT NULL,
  `pmta_smtp_port` varchar(5) NOT NULL,
  `pmta_web_monitor_port` varchar(5) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3 COLLATE=utf8mb3_bin;
/*!40101 SET character_set_client = @saved_cs_client */;
/*!40103 SET TIME_ZONE=@OLD_TIME_ZONE */;

/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;
/*!40014 SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS */;
/*!40014 SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
/*!40111 SET SQL_NOTES=@OLD_SQL_NOTES */;

-- Dump completed on 2026-09-04  8:11:43
